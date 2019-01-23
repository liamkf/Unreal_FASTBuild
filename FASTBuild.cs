// Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds. 
// Tested with Windows 10, Visual Studio 2015/2017, Unreal Engine 4.19.1, FastBuild v0.95
// Durango is fully supported (Compiles with VS2015).
// Orbis will likely require some changes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/

		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride = "";

		// Controls network build distribution
		private bool bEnableDistribution = true;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = false;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private string CachePath = @"\\SharedDrive\FASTBuildCache";

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
			WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
		}

		// Cache access mode
		// Only relevant if bEnableCaching is true;
		private eCacheMode CacheMode = eCacheMode.ReadWrite;

		/*--------------------------------------*/

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		public static bool IsAvailable()
		{
			if (FBuildExePathOverride != "")
			{
				return File.Exists(FBuildExePathOverride);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}
			return false;
		}

		private HashSet<string> ForceLocalCompileModules = new HashSet<string>()
						 {"Module.ProxyLODMeshReduction",
							"GoogleVRController"};

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					BuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.Contains("Microsoft")) //Not a great test.
				{
					BuildType = FBBuildType.Windows;
					return;
				}
			}
		}

		private bool IsMSVC() { return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne; }
		private bool IsPS4() { return BuildType == FBBuildType.PS4; }
		private bool IsXBOnePDBUtil(Action action) { return action.CommandPath.Contains("XboxOnePDBFileUtil.exe"); }
		private bool IsPS4SymbolTool(Action action) { return action.CommandPath.Contains("PS4SymbolTool.exe"); }
		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");

				List<Action> LocalExecutorActions = new List<Action>();

				if (CreateBffFile(Actions, FASTBuildFilePath, LocalExecutorActions))
				{
					FASTBuildResult = ExecuteBffFile(FASTBuildFilePath);

					if (FASTBuildResult)
					{
						LocalExecutor localExecutor = new LocalExecutor();
						FASTBuildResult = localExecutor.ExecuteActions(LocalExecutorActions, bLogDetailedActionStats);
					}
				}
				else
				{
					FASTBuildResult = false;
				}
			}

			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
			outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");

			return outputString;
		}

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, bool SaveResponseFile = false, bool SkipInputFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			if (RawTokens.Length >= 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				string responseCommandline = RawTokens[0];

				// If we had spaces inside the response file path, we need to reconstruct the path.
				for (int i = 1; i < RawTokens.Length; ++i)
				{
					responseCommandline += " " + RawTokens[i];
				}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal 
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and 
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Length; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Console.WriteLine("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}
					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			if (!SkipInputFile)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					string Token = ProcessedTokens[i];
					if (Token.Length == 0)
					{
						continue;
					}

					if (Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x" || Token == "-include") // Skip tokens with values, I for cpp includes, l for resource compiler includes
					{
						++i;
					}
					else if (!Token.StartsWith("/") && !Token.StartsWith("-") && !Token.StartsWith("\"-"))
					{
						ParsedCompilerOptions["InputFile"] = Token;
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (FileItem Item in Action.PrerequisiteItems)
				{
					if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
					{
						int DepIndex = InActions.IndexOf(Item.ProducingAction);
						if (DepIndex > ActionIndex)
						{
							NumSortErrors++;
						}
					}
				}
			}
			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					if (UsedActions.Contains(ActionIndex))
					{
						continue;
					}
					Action Action = InActions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
						{
							int DepIndex = InActions.IndexOf(Item.ProducingAction);
							if (UsedActions.Contains(DepIndex))
							{
								continue;
							}
							Actions.Add(Item.ProducingAction);
							UsedActions.Add(DepIndex);
						}
					}
					Actions.Add(Action);
					UsedActions.Add(ActionIndex);
				}
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && Actions.Contains(Item.ProducingAction))
						{
							int DepIndex = Actions.IndexOf(Item.ProducingAction);
							if (DepIndex > ActionIndex)
							{
								Console.WriteLine("Action is not topologically sorted.");
								Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
								Console.WriteLine("Dependency");
								Console.WriteLine("  {0} {1}", Item.ProducingAction.CommandPath, Item.ProducingAction.CommandArguments);
								throw new BuildException("Cyclical Dependency in action graph.");
							}
						}
					}
				}
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
			object returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			return defaultValue.ToString();
		}

		private void WriteEnvironmentSetup()
		{
			DirectoryReference VCInstallDir = null;
			string VCToolPath64 = "";
			VCEnvironment VCEnv = null;

			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				if (BuildType == FBBuildType.Windows)
				{
					VCEnv = VCEnvironment.Create(WindowsPlatform.GetDefaultCompiler(null), CppPlatform.Win64, null, null);
				}
				else if (BuildType == FBBuildType.XBOne)
				{
					// If you have XboxOne source access, uncommenting the line below will be better for selecting the appropriate version of the compiler.
					// Translate the XboxOne compiler to the right Windows compiler to set the VC environment vars correctly...
					WindowsCompiler windowsCompiler = XboxOnePlatform.GetDefaultCompiler() == XboxOneCompiler.VisualStudio2015 ? WindowsCompiler.VisualStudio2015 : WindowsCompiler.VisualStudio2017;
					VCEnv = VCEnvironment.Create(windowsCompiler, CppPlatform.Win64, null, null);
				}
			}
			catch (Exception)
			{
				Console.WriteLine("Failed to get Visual Studio environment.");
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2015:
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}


				if (!WindowsPlatform.TryGetVSInstallDir(WindowsPlatform.GetDefaultCompiler(null), out VCInstallDir))
				{
					string exceptionString = "Error: Cannot locate Visual Studio Installation.";
					Console.WriteLine(exceptionString);
					throw new BuildException(exceptionString);
				}

				VCToolPath64 = VCEnvironment.GetVCToolPath64(WindowsPlatform.GetDefaultCompiler(null), VCEnv.ToolChainDir).ToString();

				string debugVCToolPath64 = VCEnv.CompilerPath.Directory.ToString();

				AddText(string.Format(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSdkDir));

				AddText("Compiler('UE4ResourceCompiler') \n{\n");
				AddText(string.Format("\t.Executable = '{0}'\n", VCEnv.ResourceCompilerPath));
				AddText("\t.CompilerFamily  = 'custom'\n");
				AddText("}\n\n");


				AddText("Compiler('UE4Compiler') \n{\n");

				AddText(string.Format("\t.Root = '{0}'\n", VCEnv.CompilerPath.Directory));
				AddText("\t.Executable = '$Root$/cl.exe'\n");
				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/c1.dll'\n");
				AddText("\t\t'$Root$/c1xx.dll'\n");
				AddText("\t\t'$Root$/c2.dll'\n");

				if (File.Exists(FileReference.Combine(VCEnv.CompilerPath.Directory, "1033/clui.dll").ToString())) //Check English first...
				{
					AddText("\t\t'$Root$/1033/clui.dll'\n");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(VCToolPath64).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						AddText(string.Format("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First())));
					}
				}
				AddText("\t\t'$Root$/mspdbsrv.exe'\n");
				AddText("\t\t'$Root$/mspdbcore.dll'\n");

				AddText(string.Format("\t\t'$Root$/mspft{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msobj{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber));

				if (VCEnv.Compiler == WindowsCompiler.VisualStudio2015)
				{
					AddText(string.Format("\t\t'{0}/VC/redist/x64/Microsoft.VC{1}.CRT/msvcp{2}.dll'\n", VCInstallDir.ToString(), platformVersionNumber, platformVersionNumber));
					AddText(string.Format("\t\t'{0}/VC/redist/x64/Microsoft.VC{1}.CRT/vccorlib{2}.dll'\n", VCInstallDir.ToString(), platformVersionNumber, platformVersionNumber));
				}
				else
				{
					//VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
					// chose to install.
					AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.13.26020/x64/Microsoft.VC141.CRT/msvcp{1}.dll'\n", VCInstallDir.ToString(), platformVersionNumber));
					AddText(string.Format("\t\t'{0}/VC/Redist/MSVC/14.13.26020/x64/Microsoft.VC141.CRT/vccorlib{1}.dll'\n", VCInstallDir.ToString(), platformVersionNumber));
				}

				AddText("\t}\n"); //End extra files

				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText(string.Format(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText(string.Format(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				AddText("\t.ExtraFiles = '$PS4BasePath$/orbis-snarl.exe'\n");
				AddText("}\n\n");
			}

			AddText("Settings \n{\n");

			// Optional cachePath user setting
			if (bEnableCaching && CachePath != "")
			{
				AddText(string.Format("\t.CachePath = '{0}'\n", CachePath));
			}

			//Start Environment
			AddText("\t.Environment = \n\t{\n");
			if (VCEnv != null)
			{
				AddText(string.Format("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\",\n", VCInstallDir.ToString(), VCToolPath64));
				if (VCEnv.IncludePaths.Count() > 0)
				{
					AddText(string.Format("\t\t\"INCLUDE={0}\",\n", String.Join(";", VCEnv.IncludePaths.Select(x => x))));
				}

				if (VCEnv.LibraryPaths.Count() > 0)
				{
					AddText(string.Format("\t\t\"LIB={0}\",\n", String.Join(";", VCEnv.LibraryPaths.Select(x => x))));
				}
			}
			if (envVars.ContainsKey("TMP"))
				AddText(string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
			if (envVars.ContainsKey("SystemRoot"))
				AddText(string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
			if (envVars.ContainsKey("INCLUDE"))
				AddText(string.Format("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]));
			if (envVars.ContainsKey("LIB"))
				AddText(string.Format("\t\t\"LIB={0}\",\n", envVars["LIB"]));

			AddText("\t}\n"); //End environment
			AddText("}\n\n"); //End Settings
		}

		private void AddCompileAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}

			AddText(string.Format("ObjectList('Action_{0}')\n{{\n", ActionIndex));
			AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
			AddText(string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
			AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));


			bool bSkipDistribution = false;
			foreach (var it in ForceLocalCompileModules)
			{
				if (Path.GetFullPath(InputFile).Contains(it))
				{
					bSkipDistribution = true;
					break;
				}
			}


			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || bSkipDistribution)
			{
				AddText(string.Format("\t.AllowDistribution = false\n"));
			}

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
			string CompilerOutputExtension = ".unset";

			if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" {2} '\n", PCHOutputFile, PCHIncludeHeader, OtherCompilerOptions));

				AddText(string.Format("\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{0}\" {1} /Fo\"{2}\"'\n", PCHIncludeHeader, OtherCompilerOptions, OutputObjectFileName));
				AddText(string.Format("\t.PCHInputFile = \"{0}\"\n", InputFile));
				AddText(string.Format("\t.PCHOutputFile = \"{0}\"\n", PCHOutputFile));
				CompilerOutputExtension = ".obj";
			}
			else if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
				AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" /FI\"{2}\" {3} '\n", PCHOutputFile, PCHIncludeHeader, PCHToForceInclude, OtherCompilerOptions));
				CompilerOutputExtension = ".cpp.obj";
			}
			else
			{
				if (CompilerName == "UE4ResourceCompiler")
				{
					AddText(string.Format("\t.CompilerOptions = '{0} /fo\"%2\" \"%1\" '\n", OtherCompilerOptions));
					CompilerOutputExtension = Path.GetExtension(InputFile) + ".res";
				}
				else
				{
					if (IsMSVC())
					{
						AddText(string.Format("\t.CompilerOptions = '{0} /Fo\"%2\" \"%1\" '\n", OtherCompilerOptions));
						CompilerOutputExtension = ".cpp.obj";
					}
					else
					{
						AddText(string.Format("\t.CompilerOptions = '{0} -o \"%2\" \"%1\" '\n", OtherCompilerOptions));
						CompilerOutputExtension = ".cpp.o";
					}
				}
			}

			AddText(string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));

			if (DependencyIndices.Count > 0)
			{
				List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
			}

			AddText(string.Format("}}\n\n"));
		}

		private void AddLinkAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			string[] SpecialLinkerOptions = { "/OUT:", "@", "-o" };
			var ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialLinkerOptions, SaveResponseFile: true, SkipInputFile: Action.CommandPath.Contains("orbis-clang"));

			string OutputFile;

			if (IsXBOnePDBUtil(Action))
			{
				OutputFile = ParsedLinkerOptions["OtherOptions"].Trim(' ').Trim('"');
			}
			else if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else //PS4
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			List<int> PrebuildDependencies = new List<int>();

			if (IsXBOnePDBUtil(Action))
			{
				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecInput = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", ParsedLinkerOptions["InputFile"]));
				AddText(string.Format("}}\n\n"));
			}
			else if (IsPS4SymbolTool(Action))
			{
				string searchString = "-map=\"";
				int execArgumentStart = Action.CommandArguments.LastIndexOf(searchString) + searchString.Length;
				int execArgumentEnd = Action.CommandArguments.IndexOf("\"", execArgumentStart);
				string ExecOutput = Action.CommandArguments.Substring(execArgumentStart, execArgumentEnd - execArgumentStart);

				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));
				AddText(string.Format("\t.ExecOutput = '{0}'\n", ExecOutput));
				AddText(string.Format("\t.PreBuildDependencies = {{ 'Action_{0}' }} \n", ActionIndex - 1));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl"))
			{
				if (DependencyIndices.Count > 0)
				{
					for (int i = 0; i < DependencyIndices.Count; ++i) //Don't specify pch or resource files, they have the wrong name and the response file will have them anyways.
					{
						int depIndex = DependencyIndices[i];
						foreach (FileItem item in Actions[depIndex].ProducedItems)
						{
							if (item.ToString().Contains(".pch") || item.ToString().Contains(".res"))
							{
								DependencyIndices.RemoveAt(i);
								i--;
								PrebuildDependencies.Add(depIndex);
								break;
							}
						}
					}
				}

				AddText(string.Format("Library('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.Compiler = '{0}'\n", GetCompilerName()));
				if (IsMSVC())
					AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
				else
					AddText(string.Format("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n"));
				AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));
				AddText(string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

				if (!string.IsNullOrEmpty(ResponseFilePath))
				{
					if (IsMSVC())
						// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
						AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" /ignore:4042 @\"{0}\" \"%1\"' \n", ResponseFilePath));
					else if (IsPS4())
						AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\"' \n", ResponseFilePath));
					else
						AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
				}
				else
				{
					if (IsMSVC())
						AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" {0} \"%1\"' \n", OtherCompilerOptions));
				}

				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));

					if (IsPS4())
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", ResponseFilePath)); // Hack...Because FastBuild needs at least one Input file
					else if (!string.IsNullOrEmpty(ResponseFilePath))
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", DependencyNames[0])); // Hack...Because FastBuild needs at least one Input file
					else if (IsMSVC())
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));

					PrebuildDependencies.AddRange(DependencyIndices);
				}
				else
				{
					string InputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
					if (InputFile != null && InputFile.Length > 0)
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", InputFile));
				}

				if (PrebuildDependencies.Count > 0)
				{
					List<string> PrebuildDependencyNames = PrebuildDependencies.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", PrebuildDependencyNames.ToArray())));
				}

				AddText(string.Format("\t.LibrarianOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang"))
			{
				if (DependencyIndices.Count > 0) //Insert a dummy node to make sure all of the dependencies are finished.
												 //If FASTBuild supports PreBuildDependencies on the Executable action we can remove this.
				{
					string dummyText = string.IsNullOrEmpty(ResponseFilePath) ? GetOptionValue(ParsedLinkerOptions, "InputFile", Action) : ResponseFilePath;
					File.SetLastAccessTimeUtc(dummyText, DateTime.UtcNow);
					AddText(string.Format("Copy('Action_{0}_dummy')\n{{ \n", ActionIndex));
					AddText(string.Format("\t.Source = '{0}' \n", dummyText));
					AddText(string.Format("\t.Dest = '{0}' \n", dummyText + ".dummy"));
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
					AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
					AddText(string.Format("}}\n\n"));
				}

				AddText(string.Format("Executable('Action_{0}')\n{{ \n", ActionIndex));
				AddText(string.Format("\t.Linker = '{0}' \n", Action.CommandPath));

				if (DependencyIndices.Count == 0)
				{
					AddText(string.Format("\t.Libraries = {{ '{0}' }} \n", ResponseFilePath));
					if (IsMSVC())
					{
						if (BuildType == FBBuildType.XBOne)
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
						}
						else
						{
							// /ignore:4042 to turn off the linker warning about the output option being present twice (command-line + rsp file)
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /ignore:4042 /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
						}
					}
					else
						AddText(string.Format("\t.LinkerOptions = '{0} -o \"%2\" @\"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}
				else
				{
					AddText(string.Format("\t.Libraries = 'Action_{0}_dummy' \n", ActionIndex));
					if (IsMSVC())
					{
						if (BuildType == FBBuildType.XBOne)
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
						}
						else
						{
							AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
						}
					}
					else
						AddText(string.Format("\t.LinkerOptions = '{0} -o \"%2\" @\"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}

				AddText(string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
		}

		private FileStream bffOutputFileStream = null;

		private bool CreateBffFile(List<Action> InActions, string BffFilePath, List<Action> LocalExecutorActions)
		{
			List<Action> Actions = SortActions(InActions);

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				int numFastBuildActions = 0;

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null)
						{
							int ProducingActionIndex = Actions.IndexOf(Item.ProducingAction);
							if (ProducingActionIndex >= 0)
							{
								DependencyIndices.Add(ProducingActionIndex);
							}
						}
					}

					AddText(string.Format("// \"{0}\" {1}\n", Action.CommandPath, Action.CommandArguments));
					switch (Action.ActionType)
					{
						case ActionType.Compile: AddCompileAction(Action, ActionIndex, DependencyIndices); ++numFastBuildActions; break;
						case ActionType.Link: AddLinkAction(Actions, ActionIndex, DependencyIndices); ++numFastBuildActions; break;
						case ActionType.BuildProject: LocalExecutorActions.Add(Action); break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}

				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");
				for (int ActionIndex = 0; ActionIndex < numFastBuildActions; ActionIndex++)
				{
					AddText(string.Format("\t\t'Action_{0}'{1}", ActionIndex, ActionIndex < (numFastBuildActions - 1) ? ",\n" : "\n\t}\n"));
				}
				AddText("}\n");

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
				return false;
			}

			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			string cacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case eCacheMode.ReadOnly:
						cacheArgument = "-cacheread";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache";
						break;
				}
			}

			string distArgument = bEnableDistribution ? "-dist" : "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = string.Format("-monitor -summary {0} {1} -ide -clean -config {2}", distArgument, cacheArgument, BffFilePath);

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}
}
