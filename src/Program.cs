﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis.Tools.Logging;
using Microsoft.CodeAnalysis.Tools.MSBuild;
using Microsoft.CodeAnalysis.Tools.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools
{
    internal class Program
    {
        private static string[] VerbosityLevels => new[] { "q", "quiet", "m", "minimal", "n", "normal", "d", "detailed", "diag", "diagnostic" };
        internal const int UnhandledExceptionExitCode = 1;
        internal const int CheckFailedExitCode = 2;
        internal const int UnableToLocateMSBuildExitCode = 3;

        private static async Task<int> Main(string[] args)
        {
            var rootCommand = CreateCommandLineOptions();

            // Parse the incoming args and invoke the handler
            return await rootCommand.InvokeAsync(args);
        }

        public static RootCommand CreateCommandLineOptions()
        {
            var rootCommand = new RootCommand
            {
                new Argument<string>("project")
                {
                    Arity = ArgumentArity.ZeroOrOne,
                    Description = Resources.The_solution_or_project_file_to_operate_on_If_a_file_is_not_specified_the_command_will_search_the_current_directory_for_one
                },
                new Option(new[] { "--folder", "-f" }, Resources.The_folder_to_operate_on_Cannot_be_used_with_the_workspace_option)
                {
                    Argument = new Argument<string?>(() => null)
                },
                new Option(new[] { "--workspace", "-w" }, Resources.The_solution_or_project_file_to_operate_on_If_a_file_is_not_specified_the_command_will_search_the_current_directory_for_one)
                {
                    Argument = new Argument<string?>(() => null)
                },
                new Option(new[] { "--include", "--files" }, Resources.A_list_of_relative_file_or_folder_paths_to_include_in_formatting_All_files_are_formatted_if_empty)
                {
                    Argument = new Argument<string[]>(() => Array.Empty<string>())
                },
                new Option(new[] { "--exclude" }, Resources.A_list_of_relative_file_or_folder_paths_to_exclude_from_formatting)
                {
                    Argument = new Argument<string[]>(() => Array.Empty<string>())
                },
                new Option(new[] { "--check", "--dry-run" }, Resources.Formats_files_without_saving_changes_to_disk_Terminate_with_a_non_zero_exit_code_if_any_files_were_formatted)
                {
                    Argument = new Argument<bool>()
                },
                new Option(new[] { "--report" }, Resources.Accepts_a_file_path_which_if_provided_will_produce_a_format_report_json_file_in_the_given_directory)
                {
                    Argument = new Argument<string?>(() => null)
                },
                new Option(new[] { "--verbosity", "-v" }, Resources.Set_the_verbosity_level_Allowed_values_are_quiet_minimal_normal_detailed_and_diagnostic)
                {
                    Argument = new Argument<string?>() { Arity = ArgumentArity.ExactlyOne }
                },
                new Option(new[] { "--include-generated" }, Resources.Include_generated_code_files_in_formatting_operations)
                {
                    Argument = new Argument<bool>(),
                    IsHidden = true
                },
            };

            rootCommand.Description = "dotnet-format";
            rootCommand.Handler = CommandHandler.Create(typeof(Program).GetMethod(nameof(Run)));
            return rootCommand;
        }

        public static async Task<int> Run(string? folder, string? workspace, string? verbosity, bool check, string[] include, string[] exclude, string? report, bool includeGenerated, IConsole console = null!)
        {
            // Setup logging.
            var serviceCollection = new ServiceCollection();
            var logLevel = GetLogLevel(verbosity);
            ConfigureServices(serviceCollection, console, logLevel);

            var serviceProvider = serviceCollection.BuildServiceProvider();
            var logger = serviceProvider.GetService<ILogger<Program>>();

            // Hook so we can cancel and exit when ctrl+c is pressed.
            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            var currentDirectory = string.Empty;

            try
            {
                currentDirectory = Environment.CurrentDirectory;

                string workspaceDirectory;
                string workspacePath;
                WorkspaceType workspaceType;

                if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(workspace))
                {
                    logger.LogWarning(Resources.Cannot_specify_both_folder_and_workspace_options);
                    return 1;
                }
                else if (!string.IsNullOrEmpty(workspace))
                {
                    var (isSolution, workspaceFilePath) = MSBuildWorkspaceFinder.FindWorkspace(currentDirectory, workspace);

                    workspacePath = workspaceFilePath;
                    workspaceType = isSolution
                        ? WorkspaceType.Solution
                        : WorkspaceType.Project;

                    // To ensure we get the version of MSBuild packaged with the dotnet SDK used by the
                    // workspace, use its directory as our working directory which will take into account
                    // a global.json if present.
                    var directoryName = Path.GetDirectoryName(workspacePath);
                    if (directoryName is null)
                        throw new Exception($"Unable to find folder at '{workspacePath}'");
                    workspaceDirectory = directoryName;
                }
                else
                {
                    // If folder isn't populated, then use the current directory
                    folder = Path.GetFullPath(folder ?? ".", Environment.CurrentDirectory);
                    workspacePath = folder;
                    workspaceDirectory = workspacePath;
                    workspaceType = WorkspaceType.Folder;
                }

                Environment.CurrentDirectory = workspaceDirectory;

                try
                {
                    // Since we are running as a dotnet tool we should be able to find an instance of
                    // MSBuild in a .NET Core SDK.
                    var msBuildInstance = Build.Locator.MSBuildLocator.QueryVisualStudioInstances().First();

                    // Since we do not inherit msbuild.deps.json when referencing the SDK copy
                    // of MSBuild and because the SDK no longer ships with version matched assemblies, we
                    // register an assembly loader that will load assemblies from the msbuild path with
                    // equal or higher version numbers than requested.
                    LooseVersionAssemblyLoader.Register(msBuildInstance.MSBuildPath);
                    Build.Locator.MSBuildLocator.RegisterInstance(msBuildInstance);

                    if (logLevel == LogLevel.Trace)
                    {
                        logger.LogInformation(Resources.Using_msbuildexe_located_in_0, msBuildInstance.MSBuildPath);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Unable to find MSBuild instance.
                    logger.LogError(Resources.Unable_to_locate_MSBuild_Ensure_the_NET_SDK_was_installed_with_the_official_installer);
                    return UnableToLocateMSBuildExitCode;
                }

                var fileMatcher = SourceFileMatcher.CreateMatcher(include, exclude);

                var formatOptions = new FormatOptions(
                    workspacePath,
                    workspaceType,
                    logLevel,
                    saveFormattedFiles: !check,
                    changesAreErrors: check,
                    fileMatcher,
                    reportPath: report,
                    includeGenerated);

                var formatResult = await CodeFormatter.FormatWorkspaceAsync(
                    formatOptions,
                    logger,
                    cancellationTokenSource.Token,
                    createBinaryLog: logLevel == LogLevel.Trace).ConfigureAwait(false);

                return GetExitCode(formatResult, check);
            }
            catch (FileNotFoundException fex)
            {
                logger.LogError(fex.Message);
                return UnhandledExceptionExitCode;
            }
            catch (OperationCanceledException)
            {
                return UnhandledExceptionExitCode;
            }
            finally
            {
                if (!string.IsNullOrEmpty(currentDirectory))
                {
                    Environment.CurrentDirectory = currentDirectory;
                }
            }
        }

        internal static int GetExitCode(WorkspaceFormatResult formatResult, bool check)
        {
            if (!check)
            {
                return formatResult.ExitCode;
            }

            return formatResult.FilesFormatted == 0 ? 0 : CheckFailedExitCode;
        }

        internal static LogLevel GetLogLevel(string? verbosity)
        {
            switch (verbosity)
            {
                case "q":
                case "quiet":
                    return LogLevel.Error;
                case "m":
                case "minimal":
                    return LogLevel.Warning;
                case "n":
                case "normal":
                    return LogLevel.Information;
                case "d":
                case "detailed":
                    return LogLevel.Debug;
                case "diag":
                case "diagnostic":
                    return LogLevel.Trace;
                default:
                    return LogLevel.Information;
            }
        }

        private static void ConfigureServices(ServiceCollection serviceCollection, IConsole console, LogLevel logLevel)
        {
            serviceCollection.AddSingleton(new LoggerFactory().AddSimpleConsole(console, logLevel));
            serviceCollection.AddLogging();
        }
    }
}
