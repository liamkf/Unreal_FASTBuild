// Copyright 2016 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds. 
// Requires fbuild.exe to be in the path.
// Predominately tested with Win10/VS2015.
// Different toolchains like Durango and Orbis will likely require some modifications.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace UnrealBuildTool
{
	public class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/


		// Controls network build distribution
		private bool bEnableDistribution = true;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled. 
		private bool bEnableCaching = false;

		// Location of the shared cache, it could be a local or network path (i.e: "\\\\DESKTOP-BEAST\\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private string CachePath = ""; //"\\\\DESKTOP-BEAST\\FASTBuildCache";   

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
			if(FBuildExePathOverride != "")
			{
				return File.Exists(FBuildExePathOverride);
			}		

			// Get the name of the XgConsole executable.
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



		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach(Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandPath.Contains("Durango"))
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
		private bool IsXBOnePDBUtil(Action action) { return action.CommandPath.Contains("XboxOnePDBFileUtil.exe"); }
		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
				case FBBuildType.XBOne: return "UE4XBoneCompiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);
				string FASTBuildFilePath = Path.Combine(BuildConfiguration.BaseIntermediatePath, "fbuild.bff");
				CreateBffFile(Actions, FASTBuildFilePath);
				return ExecuteBffFile(FASTBuildFilePath);
			}
			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, bool SaveResponseFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string,string>();

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			if (RawTokens.Length == 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				ResponseFilePath = RawTokens[0].Substring(2, RawTokens[0].Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					string[] separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = File.ReadAllText(ResponseFilePath).Split(separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal 
				}
				catch(Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and 
			// paths with multiple spaces in them also need some love
			for(int i=0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if(string.IsNullOrEmpty(Token))
				{
					if(ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for(int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
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
						for( ; i < RawTokens.Length; ++i)
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
						if(!AddedToken)
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
			foreach(string specialOption in SpecialOptions)
			{
				for(int i=0; i < ProcessedTokens.Count; ++i)
				{
					if(ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];			
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if(ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			for (int i = 0; i < ProcessedTokens.Count; ++i)
			{
				string Token = ProcessedTokens[i];
				if(Token.Length == 0)
				{
					continue;
				}

				if(Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x") // Skip tokens with values, I for cpp includes, l for resource compiler includes
				{
					++i;
				}
				else if(!Token.StartsWith("/") && !Token.StartsWith("-"))
				{
					ParsedCompilerOptions["InputFile"] = Token;
					ProcessedTokens.RemoveAt(i);
					break;
				} 
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if(SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
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
			if(OptionsDictionary.TryGetValue(Key, out Value))
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
			VCEnvironment VCEnv = null;
			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				VCEnv = VCEnvironment.SetEnvironment(CPPTargetPlatform.Win64, false);
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
				AddText(string.Format(".CommonProgramFiles = '{0}'\n", envVars["CommonProgramFiles"]));
			}

			if (VCEnv != null)
			{
				AddText(string.Format(".VSBasePath = '{0}\\'\n", VCEnv.VSInstallDir));
				AddText(string.Format(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSDKDir));

				AddText("Compiler('UE4ResourceCompiler') \n{\n");
				AddText("\t.Executable = '$WindowsSDKBasePath$/bin/x64/rc.exe'\n}\n\n");

				AddText("Compiler('UE4Compiler') \n{\n");
				AddText("\t.Root = '$VSBasePath$/VC/bin'\n");
				AddText("\t.Executable = '$Root$/amd64/cl.exe'\n");
				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/amd64/c1.dll'\n");
				AddText("\t\t'$Root$/amd64/c1xx.dll'\n");
				AddText("\t\t'$Root$/amd64/c2.dll'\n");
				string CompilerRoot = VCEnv.VCToolPath64.ToString() + "/";
				if (File.Exists(CompilerRoot + "1033/clui.dll")) //Check English first...
				{
					AddText("\t\t'$Root$/1033/clui.dll'\n");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(CompilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						AddText(string.Format("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First())));
					}
				}
				AddText("\t\t'$Root$/amd64/mspdbsrv.exe'\n");
				AddText("\t\t'$Root$/amd64/mspdbcore.dll'\n");

				string platformVersionNumber = "140";
				if (WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2013)
				{
					platformVersionNumber = "120";
				}

				/* Maybe not needed to compile anymore?
				if(!WindowsPlatform.bUseWindowsSDK10)
					AddText(string.Format("\t\t'$VSBasePath$/VC/redist/x64/Microsoft.VC{0}.CRT/msvcr{1}.dll'\n", platformVersionNumber, platformVersionNumber));
				else
					AddText("\t\t'$WindowsSDKBasePath$/Redist/ucrt/DLLs/x64/ucrtbase.dll'\n\n");
				*/
				AddText(string.Format("\t\t'$Root$/amd64/mspft{0}.dll'\n",platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/amd64/msobj{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$Root$/amd64/mspdb{0}.dll'\n", platformVersionNumber));
				AddText(string.Format("\t\t'$VSBasePath$/VC/redist/x64/Microsoft.VC{0}.CRT/msvcp{1}.dll'\n", platformVersionNumber, platformVersionNumber));
				AddText(string.Format("\t\t'$VSBasePath$/VC/redist/x64/Microsoft.VC{0}.CRT/vccorlib{1}.dll'\n", platformVersionNumber, platformVersionNumber));
				AddText("\t}\n"); //End extra files

				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText(string.Format("\n.DurangoXDK = '{0}'\n", envVars["DurangoXDK"]));
				AddText("Compiler('UE4XBOneCompiler') \n{\n");
				AddText("\t.Root = '$DurangoXDK$/xdk/VC/bin/amd64'\n");
				AddText("\t.Executable = '$Root$/cl.exe'\n");
				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/c1.dll'\n");
				AddText("\t\t'$Root$/c1ast.dll'\n");
				AddText("\t\t'$Root$/c1xx.dll'\n");
				AddText("\t\t'$Root$/c1xxast.dll'\n");
				AddText("\t\t'$Root$/c2.dll'\n");
				AddText("\t\t'$Root$/1033/clui.dll'\n");
				AddText("\t\t'$Root$/mspdbsrv.exe'\n");
				AddText("\t\t'$Root$/mspdbcore.dll'\n");
				AddText("\t\t'$Root$/vcmeta.dll'\n");
				string XBplatformVersionNumber = "110"; //Todo: This is a bodge. Check durango env vars?
				AddText(string.Format("\t\t'$Root$/mspft{0}.dll'\n", XBplatformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msobj{0}.dll'\n", XBplatformVersionNumber));
				AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", XBplatformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msvcp{0}.dll'\n", XBplatformVersionNumber));
				AddText(string.Format("\t\t'$Root$/msvcr{0}.dll'\n", XBplatformVersionNumber));
				AddText(string.Format("\t\t'$Root$/vccorlib{0}.dll'\n", XBplatformVersionNumber));
				AddText("\t\t'$DurangoXDK$/xdk/crt/platform/amd64/platform.winmd'\n");
				AddText("\t\t'$DurangoXDK$/xdk/References/CommonConfiguration/Neutral/Windows.winmd'\n");
				AddText("\t\t'$DurangoXDK$/xdk/Extensions/Xbox Services API/8.0/References/CommonConfiguration/neutral/microsoft.xbox.services.winmd'\n");
				AddText("\t\t'$DurangoXDK$/xdk/Extensions/Xbox GameChat API/8.0/References/CommonConfiguration/neutral/Microsoft.Xbox.GameChat.winmd'\n");
				AddText("\t}\n"); //End extra files
				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText(string.Format(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText(string.Format(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
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
			AddText("\t\t\"PATH=$VSBasePath$\\Common7\\IDE\\;$VSBasePath$\\VC\\bin\\\",\n");
			if (envVars.ContainsKey("TMP"))
				AddText(string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
			if (envVars.ContainsKey("SystemRoot"))
				AddText(string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
			if(envVars.ContainsKey("INCLUDE"))
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
			if(string.IsNullOrEmpty(InputFile))
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}

			AddText(string.Format("ObjectList('Action_{0}')\n{{\n", ActionIndex));
			AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
			AddText(string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
			AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));

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
					if(IsMSVC())
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
			var ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialLinkerOptions, SaveResponseFile: true);

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
				if(string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true);
				}
			}
			
			if(string.IsNullOrEmpty(OutputFile))
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
				AddText(string.Format("\t.Compiler = '{0}'\n",GetCompilerName()));
				if(IsMSVC())
					AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
				else
					AddText(string.Format("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n"));
				AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));
				AddText(string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

				if (!string.IsNullOrEmpty(ResponseFilePath))
				{
					if(IsMSVC())
					{
						if (DependencyIndices.Count > 0)
							AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" @\"{0}\" {1} \"%1\"' \n", ResponseFilePath, OtherCompilerOptions));
						else
							AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
					}						
					else
						AddText(string.Format("\t.LibrarianOptions = '\"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
				}
				else
				{
					if(IsMSVC())
						AddText(string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" {0} \"%1\"' \n", OtherCompilerOptions));
				}

				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					if(IsMSVC())
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));
					else if(!string.IsNullOrEmpty(ResponseFilePath))
						AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", ResponseFilePath));
					PrebuildDependencies.AddRange(DependencyIndices);
				}
				else if(!string.IsNullOrEmpty(ResponseFilePath))
				{
					AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", ResponseFilePath));
				}
				else
				{
					AddText(string.Format("\t.LibrarianAdditionalInputs = {{ '{0}' }} \n", GetOptionValue(ParsedLinkerOptions, "InputFile", Action, ProblemIfNotFound: true)));
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
				if(DependencyIndices.Count == 0)
				{
					AddText(string.Format("\t.Libraries = {{ '{0}' }} \n", ResponseFilePath));
					if (IsMSVC())
						AddText(string.Format("\t.LinkerOptions = '@\"%1\" /Out:\"%2\" {0}' \n", OtherCompilerOptions));
					else
						AddText(string.Format("\t.LinkerOptions = '-o \"%2\" @\"%1\" {0}' \n", OtherCompilerOptions));
				}
				else
				{
					AddText(string.Format("\t.Libraries = 'Action_{0}_dummy' \n", ActionIndex));
					if(IsMSVC())
						AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
					else
						AddText(string.Format("\t.LinkerOptions = '-o \"%2\" @\"{0}\" {1} -MQ \"%1\"' \n", ResponseFilePath, OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}
				
				AddText(string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
		}

		private FileStream bffOutputFileStream = null;

		private void CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			List<Action> Actions = SortActions(InActions);

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);
	
				WriteEnvironmentSetup(); //Compiler, environment variables and base paths
	
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

					Action.CommandArguments = Action.CommandArguments.Replace("$(DurangoXDK)", "$DurangoXDK$");
					Action.CommandArguments = Action.CommandArguments.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
					Action.CommandArguments = Action.CommandArguments.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
					Action.CommandArguments = Action.CommandArguments.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");
	
					switch(Action.ActionType)
					{
						case ActionType.Compile : AddCompileAction(Action, ActionIndex, DependencyIndices); break;
						case ActionType.Link: AddLinkAction(Actions, ActionIndex, DependencyIndices); break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}
	
				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					AddText(string.Format("\t\t'Action_{0}'{1}", ActionIndex, ActionIndex < (Actions.Count - 1) ? ",\n" : "\n\t}\n"));
				}
				AddText("}\n");

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
			}
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


			string FBCommandLine = string.Format("-summary {0} {1} -ide -config {2}", distArgument, cacheArgument, BffFilePath);

			//Interesting flags for FASTBuild: -nostoponerror, -verbose	
			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(BuildConfiguration.RelativeEnginePath, "Source");

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) => {
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
