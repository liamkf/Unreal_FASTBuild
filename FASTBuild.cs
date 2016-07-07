// Copyright 2015 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds. 
// Requires fbuild.exe to be in the path.
// As written only supports Win10/VS2015. Other VS toolchains (including Durango) require minor modifications.
// PS4 is also doable, but using sndbs is probably easier.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;

namespace UnrealBuildTool
{
	public class FASTBuild
	{
		public enum ExecutionResult
		{
			Unavailable,
			TasksFailed,
			TasksSucceeded,
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public static ExecutionResult ExecuteActions(List<Action> Actions)
		{
			if(!(WindowsPlatform.Compiler == WindowsCompiler.VisualStudio2015 && WindowsPlatform.bUseWindowsSDK10))
			{
				Console.WriteLine("Warning: FASTBuild building without both VS2015 and the Win10 SDK is probably not going to work without changes!");
			}

			ExecutionResult FASTBuildResult = ExecutionResult.TasksSucceeded;
			if (Actions.Count > 0)
			{
				string FASTBuildFilePath = Path.Combine(BuildConfiguration.BaseIntermediatePath, "fbuild.bff");
                CreateBffFile(Actions, FASTBuildFilePath);
				return ExecuteBffFile(FASTBuildFilePath);
			}
			return FASTBuildResult;
		}

		private static void AddText(FileStream DestinationFile, string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			DestinationFile.Write(Info, 0, Info.Length);
		}

		private static Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] specialOptions)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string,string>();

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";

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
				if(Token.StartsWith("/D") && !QuotesOpened)
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
			foreach(string specialOption in specialOptions)
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

				if(Token == "/I" || Token == "/l") // Skip tokens with values, I for cpp includes, l for resource compiler includes
				{
					++i;
				}
				else if(!Token.StartsWith("/"))
				{
					ParsedCompilerOptions["InputFile"] = Token;
					ProcessedTokens.RemoveAt(i);
					break;
				} 
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens);

			return ParsedCompilerOptions;
		}

		private static string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if(OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find" + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		private static void WriteEnvironmentSetup(FileStream FbOutputFileStream)
		{
			VCEnvironment VCEnv = VCEnvironment.SetEnvironment(CPPTargetPlatform.Win64, false);

			IDictionary envVars = Environment.GetEnvironmentVariables();

			AddText(FbOutputFileStream, string.Format(".VSBasePath = '{0}..\\'\n", VCEnv.VisualCppDir));
			AddText(FbOutputFileStream, string.Format(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSDKDir));
			if(envVars.Contains("CommonProgramFiles"))
				AddText(FbOutputFileStream, string.Format(".CommonProgramFiles = '{0}'\n\n", envVars["CommonProgramFiles"]));

			//Start compiler
			AddText(FbOutputFileStream, "Compiler('UE4ResourceCompiler') \n{\n");
			AddText(FbOutputFileStream, "\t.Executable = '$WindowsSDKBasePath$/bin/x64/rc.exe'\n}\n\n");

			AddText(FbOutputFileStream, "Compiler('UE4Compiler') \n{\n");
			AddText(FbOutputFileStream, "\t.Root = '$VSBasePath$/VC/bin'\n");
			AddText(FbOutputFileStream, "\t.Executable = '$Root$/amd64/cl.exe'\n");
			AddText(FbOutputFileStream, "\t.ExtraFiles =\n\t\t{\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/c1.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/c1xx.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/c2.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/1033/clui.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/mspft140.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/msobj140.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/mspdb140.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/mspdbsrv.exe'\n");
				AddText(FbOutputFileStream, "\t\t'$Root$/amd64/mspdbcore.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$VSBasePath$/VC/redist/x64/Microsoft.VC140.CRT/msvcp140.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$VSBasePath$/VC/redist/x64/Microsoft.VC140.CRT/vccorlib140.dll'\n");
				AddText(FbOutputFileStream, "\t\t'$WindowsSDKBasePath$/Redist/ucrt/DLLs/x64/ucrtbase.dll'\n\t\t}\n");
			AddText(FbOutputFileStream, "}\n\n");
			//End compiler

			AddText(FbOutputFileStream, "Settings \n{\n");

			//Start Environment
			AddText(FbOutputFileStream, "\t.Environment = \n\t{\n");
			AddText(FbOutputFileStream, "\t\t\"PATH=$VSBasePath$\\Common7\\IDE\\;$VSBasePath$\\VC\\bin\\\",\n");
			if (envVars.Contains("TMP"))
				AddText(FbOutputFileStream, string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
			if (envVars.Contains("SystemRoot"))
				AddText(FbOutputFileStream, string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
			if(envVars.Contains("INCLUDE"))
				AddText(FbOutputFileStream, string.Format("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]));
			if (envVars.Contains("LIB"))
				AddText(FbOutputFileStream, string.Format("\t\t\"LIB={0}\",\n", envVars["LIB"]));

			AddText(FbOutputFileStream, "\t}\n"); //End environment
			AddText(FbOutputFileStream, "}\n\n"); //End Settings
		}

		private static void AddCompileAction(FileStream FbOutputFileStream, Action Action, int ActionIndex, List<string> DependencyNames)
		{
			string CompilerName = "UE4Compiler";
			string CompilerOutputExtension = ".cpp.obj";

			if (Action.CommandDescription != null && Action.CommandDescription.ToLower() == "resource")
			{
				CompilerName = "UE4ResourceCompiler";
				CompilerOutputExtension = ".rc.res";
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp" };
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/Fo", Action);

			if (string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
				if(string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
				{
					Console.WriteLine("We have no OutputObjectFileName. Bailing.");
					return;
				}
			}

			int IndexOfLastSlash = OutputObjectFileName.LastIndexOf('\\');
			string IntermediatePath = IndexOfLastSlash >= 0 ? OutputObjectFileName.Substring(0, IndexOfLastSlash) : string.Empty;
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

			AddText(FbOutputFileStream, string.Format("ObjectList('Action_{0}')\n{{\n", ActionIndex));
			AddText(FbOutputFileStream, string.Format("\t.Compiler = '{0}' \n", CompilerName));
			AddText(FbOutputFileStream, string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));
			AddText(FbOutputFileStream, string.Format("\t.CompilerInputFiles = \"{0}\"\n", InputFile));
			AddText(FbOutputFileStream, string.Format("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath));

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);

			// Create PCH
			if (ParsedCompilerOptions.ContainsKey("/Yc"))
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AddText(FbOutputFileStream, string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" {2} '\n", PCHOutputFile, PCHIncludeHeader, OtherCompilerOptions));

				AddText(FbOutputFileStream, string.Format("\t.PCHOptions = '\"%1\" /Fo\"%3\" /Fp\"%2\" /Yc\"{0}\" {1}'\n", PCHIncludeHeader, OtherCompilerOptions));
				AddText(FbOutputFileStream, string.Format("\t.PCHInputFile = \"{0}\"\n", InputFile));
				AddText(FbOutputFileStream, string.Format("\t.PCHOutputFile = \"{0}\"\n", PCHOutputFile));
				AddText(FbOutputFileStream, string.Format("\t.PCHObjectNameOverride = \"{0}\"\n", OutputObjectFileName));
			}
			else if (ParsedCompilerOptions.ContainsKey("/Yu"))
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AddText(FbOutputFileStream, string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" {2} '\n", PCHOutputFile, PCHIncludeHeader, OtherCompilerOptions));
			}
			else
			{
				AddText(FbOutputFileStream, string.Format("\t.CompilerOptions = '{0} /Fo\"%2\" \"%1\"'\n", OtherCompilerOptions));
				if (CompilerName == "UE4ResourceCompiler")
				{
					AddText(FbOutputFileStream, string.Format("\t.CompilerObjectNameOverride = \"{0}\"\n", OutputObjectFileName));
				}
			}

			if (DependencyNames.Count > 0)
			{
				AddText(FbOutputFileStream, string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
			}

			AddText(FbOutputFileStream, string.Format("}}\n\n"));
		}

		private static void AddLinkAction(FileStream FbOutputFileStream, Action Action, int ActionIndex, List<string> DependencyNames)
		{ 
			string[] SpecialLinkerOptions = { "/OUT:", "@" };
			var ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialLinkerOptions);

			string OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			if (Action.CommandPath.Contains("lib.exe"))
			{
				AddText(FbOutputFileStream, string.Format("Library('Action_{0}')\n{{\n", ActionIndex));
				AddText(FbOutputFileStream,				  "\t.Compiler = 'UE4Compiler'\n");
				AddText(FbOutputFileStream, string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
				AddText(FbOutputFileStream, string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));
				AddText(FbOutputFileStream, string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

				if(ResponseFilePath.Length > 0)
				{
					AddText(FbOutputFileStream, string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" @{0} {1} \"%1\"' \n", ResponseFilePath, OtherCompilerOptions));
				}
				else
				{
					AddText(FbOutputFileStream, string.Format("\t.LibrarianOptions = ' /OUT:\"%2\" {0} \"%1\"' \n", OtherCompilerOptions));
				}

				if (DependencyNames.Count > 0)
				{
					AddText(FbOutputFileStream, string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));
					AddText(FbOutputFileStream, string.Format("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray())));
				}
				else
				{
					// Bit of a last ditch effort to fix fbuild from complaining when Unreal knows that dependencies are already built
					// so they don't show up as actions. The prebuilt depedencies are in the response file, so we just put the first line of the response as an input.
					if(ResponseFilePath.Length > 0)
					{
						AddText(FbOutputFileStream, string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", File.ReadLines(ResponseFilePath).First()));
					}
					else
					{
						Console.WriteLine("No inputs for the library, Fastbuild will not be happy!");
					}
				}

				AddText(FbOutputFileStream, string.Format("\t.LibrarianOutput = '{0}' \n", OutputFile));
				AddText(FbOutputFileStream, string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.Contains("link.exe"))
			{
				AddText(FbOutputFileStream, string.Format("Executable('Action_{0}')\n{{\n", ActionIndex));
				AddText(FbOutputFileStream, string.Format("\t.Linker = '{0}' \n", Action.CommandPath));
				AddText(FbOutputFileStream, string.Format("\t.LinkerOptions = '\"%1\" /Out:\"%2\" @{0} {1}' \n", ResponseFilePath, OtherCompilerOptions));
				AddText(FbOutputFileStream, string.Format("\t.Libraries = {{ {0} }} \n", string.Join(",", DependencyNames.ToArray())));
				AddText(FbOutputFileStream, string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));
				AddText(FbOutputFileStream, string.Format("}}\n\n"));
			}
		}

		private static void CreateBffFile(List<Action> Actions, string BffFilePath)
		{
			try
			{
				FileStream FbOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);
	
				WriteEnvironmentSetup(FbOutputFileStream); //Compiler, environment variables and base paths
	
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
	
					// Resolve dependencies
					List<string> DependencyNames = new List<string>();
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && Actions.Contains(Item.ProducingAction))
						{
							DependencyNames.Add(string.Format("'Action_{0}'", Actions.IndexOf(Item.ProducingAction)));
						}
					}
	
					Action.CommandArguments = Action.CommandArguments.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
					Action.CommandArguments = Action.CommandArguments.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");
	
					switch(Action.ActionType)
					{
						case ActionType.Compile : AddCompileAction(FbOutputFileStream, Action, ActionIndex, DependencyNames); break;
						case ActionType.Link: AddLinkAction(FbOutputFileStream, Action, ActionIndex, DependencyNames); break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}
	
				AddText(FbOutputFileStream, "Alias( 'all' ) \n{\n");
				AddText(FbOutputFileStream, "\t.Targets = { \n");
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					AddText(FbOutputFileStream,string.Format("\t\t'Action_{0}'{1}", ActionIndex, ActionIndex < (Actions.Count - 1) ? ",\n" : "\n\t}\n"));
				}
				AddText(FbOutputFileStream, "}\n");
	
				FbOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
			}
    }

		private static ExecutionResult ExecuteBffFile(string BffFilePath)
		{
			//Interesting flags for FASTBuild: -cache (can also be controlled by environment variables), -nostoponerror, -verbose
			ProcessStartInfo FBStartInfo = new ProcessStartInfo("fbuild", "-summary -ide -dist -config " + BffFilePath);

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
				return FBProcess.ExitCode == 0 ? ExecutionResult.TasksSucceeded : ExecutionResult.TasksFailed;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return ExecutionResult.Unavailable;
			}
		}
	}
}
