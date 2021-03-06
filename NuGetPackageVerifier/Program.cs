﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NuGet;
using NuGetPackageVerifier.Logging;
using NuGetPackageVerifier.Rules;

namespace NuGetPackageVerifier
{
    public class PackageSet
    {
        // Class names of rules to use
        public string[] Rules { get; set; }

        // List of packages(key), each with a set of rules to ignore(key), each with a set of instances(key), each of which has a justification(value)
        public IDictionary<string, IDictionary<string, IDictionary<string, string>>> Packages { get; set; }
    }

    public static class Program
    {
        private const int ReturnOk = 0;
        private const int ReturnBadArgs = 1;
        private const int ReturnErrorsOrWarnings = 2;

        public static int Main(string[] args)
        {
            // TODO: Take a switch saying whether to use TeamCity logger

            // TODO: Show extraneous packages, exclusions, etc.

            // TODO: Get this from the command line
            var ignoreAssistanceMode = IgnoreAssistanceMode.None;

            ignoreAssistanceMode = IgnoreAssistanceMode.ShowNew;

            if (args.Length < 1 || args.Length > 2)
            {
                Console.WriteLine(@"USAGE: NuGetSuperBVT.exe c:\path\to\packages [c:\path\to\packages-to-scan.json]");
                return ReturnBadArgs;
            }

            var logger = new PackageVerifierLogger();

            IDictionary<string, PackageSet> packageSets = null;

            if (args.Length >= 2)
            {
                string packagesToScanJsonFilePath = args[1];
                if (!File.Exists(packagesToScanJsonFilePath))
                {
                    logger.LogError("Couldn't find packages JSON file at {0}", packagesToScanJsonFilePath);
                    return ReturnBadArgs;
                }

                string packagesToScanJsonFileContent = File.ReadAllText(packagesToScanJsonFilePath);

                packageSets = JsonConvert.DeserializeObject<IDictionary<string, PackageSet>>(packagesToScanJsonFileContent, new JsonSerializerSettings()
                {
                    MissingMemberHandling = MissingMemberHandling.Error
                });

                logger.LogInfo("Read {0} package set(s) from {1}", packageSets.Count, packagesToScanJsonFilePath);
            }

            var totalTimeStopWatch = Stopwatch.StartNew();

            var nupkgsPath = args[0];


            // TODO: Look this up using reflection or something
            var allRules = new IPackageVerifierRule[] {
                new AssemblyHasDocumentFileRule(),
                new AssemblyHasVersionAttributesRule(),
                new AssemblyHasServicingAttributeRule(),
                new AssemblyStrongNameRule(),
                new AuthenticodeSigningRule(),
                new PowerShellScriptIsSignedRule(),
                new RequiredPackageMetadataRule(),
                new SatellitePackageRule(),
                new StrictSemanticVersionValidationRule(),
            }.ToDictionary(t => t.GetType().Name, t => t);


            var localPackageRepo = new LocalPackageRepository(nupkgsPath);

            var numPackagesInRepo = localPackageRepo.GetPackages().Count();
            logger.LogInfo("Found {0} packages in {1}", numPackagesInRepo, nupkgsPath);

            var processedPackages = new HashSet<IPackage>();

            var totalErrors = 0;
            var totalWarnings = 0;

            var ignoreAssistanceData = new Dictionary<string, IDictionary<string, IDictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var packageSet in packageSets)
            {
                logger.LogInfo("Processing package set '{0}' with {1} package(s)", packageSet.Key, packageSet.Value.Packages.Count);

                var packageSetRuleInfo = packageSet.Value.Rules;

                var packageSetRules = packageSetRuleInfo.Select(ruleId => allRules.Single(rule => string.Equals(rule.Key, ruleId, StringComparison.OrdinalIgnoreCase)).Value);

                var analyzer = new PackageAnalyzer();
                foreach (var ruleInstance in packageSetRules)
                {
                    analyzer.Rules.Add(ruleInstance);
                }

                IList<IssueIgnore> issuesToIgnore = GetIgnoresFromFile(packageSet.Value.Packages);

                var issueProcessor = new IssueProcessor(issuesToIgnore);


                foreach (var packageInfo in packageSet.Value.Packages)
                {
                    var packageId = packageInfo.Key;
                    var packageIgnoreInfo = packageInfo.Value;

                    var packagesWithId = localPackageRepo.FindPackagesById(packageId);
                    if (!packagesWithId.Any())
                    {
                        logger.LogError("Couldn't find package '{0}' in the repo", packageId);
                        totalErrors++;
                        continue;
                    }
                    if (packagesWithId.Count() > 1)
                    {
                        logger.LogError("Found more than one package with id '{0}' in the repo", packageId);
                        totalErrors++;
                        continue;
                    }
                    var package = packagesWithId.Single();

                    var packageTimeStopWatch = Stopwatch.StartNew();
                    logger.LogInfo("Analyzing {0} ({1})", package.Id, package.Version);


                    var issues = analyzer.AnalyzePackage(localPackageRepo, package, logger).ToList();

                    var packageErrorsAndWarnings = ProcessPackageIssues(
                        ignoreAssistanceMode, logger, issueProcessor,
                        ignoreAssistanceData, package, issues);

                    totalErrors += packageErrorsAndWarnings.Item1;
                    totalWarnings += packageErrorsAndWarnings.Item2;


                    packageTimeStopWatch.Stop();
                    logger.LogInfo("Took {0}ms", packageTimeStopWatch.ElapsedMilliseconds);
                    Console.WriteLine();

                    processedPackages.Add(package);
                }
            }

            var unprocessedPackages = localPackageRepo.GetPackages().Except(processedPackages);

            if (unprocessedPackages.Any())
            {
                logger.LogWarning("Found {0} unprocessed packages. Every package in the repo should be listed in exactly one package set. Running all rules on unlisted packages.", unprocessedPackages.Count());

                // For unprocessed packages we run all rules (because we have no idea what exactly to run)
                var analyzer = new PackageAnalyzer();
                foreach (var ruleInstance in allRules.Values)
                {
                    analyzer.Rules.Add(ruleInstance);
                }

                var issueProcessor = new IssueProcessor(issuesToIgnore: null);

                foreach (var unprocessedPackage in unprocessedPackages)
                {
                    logger.LogWarning("\tUnprocessed package: {0} ({1})", unprocessedPackage.Id, unprocessedPackage.Version);

                    var packageTimeStopWatch = Stopwatch.StartNew();
                    logger.LogInfo("Analyzing {0} ({1})", unprocessedPackage.Id, unprocessedPackage.Version);


                    var issues = analyzer.AnalyzePackage(localPackageRepo, unprocessedPackage, logger).ToList();

                    var packageErrorsAndWarnings = ProcessPackageIssues(
                        ignoreAssistanceMode, logger, issueProcessor,
                        ignoreAssistanceData, unprocessedPackage, issues);

                    totalErrors += packageErrorsAndWarnings.Item1;
                    totalWarnings += packageErrorsAndWarnings.Item2;


                    packageTimeStopWatch.Stop();
                    logger.LogInfo("Took {0}ms", packageTimeStopWatch.ElapsedMilliseconds);
                    Console.WriteLine();
                }
            }


            if (ignoreAssistanceMode != IgnoreAssistanceMode.None)
            {
                Console.WriteLine("Showing JSON for ignore content:");
                Console.WriteLine(JsonConvert.SerializeObject(ignoreAssistanceData, Formatting.Indented));
                Console.WriteLine();
            }

            LogLevel errorLevel = LogLevel.Info;
            if (totalWarnings > 0)
            {
                errorLevel = LogLevel.Warning;
            }
            if (totalErrors > 0)
            {
                errorLevel = LogLevel.Error;
            }
            logger.Log(
                errorLevel,
                "SUMMARY: {0} error(s) and {1} warning(s) found",
                totalErrors, totalWarnings);

            totalTimeStopWatch.Stop();
            logger.LogInfo("Total took {0}ms", totalTimeStopWatch.ElapsedMilliseconds);

            Console.ReadLine();

            return (totalErrors + totalWarnings > 0) ? ReturnErrorsOrWarnings : ReturnOk;
        }

        private static Tuple<int, int> ProcessPackageIssues(
            IgnoreAssistanceMode ignoreAssistanceMode,
            PackageVerifierLogger logger,
            IssueProcessor issueProcessor,
            Dictionary<string, IDictionary<string, IDictionary<string, string>>> ignoreAssistanceData,
            IPackage package,
            List<PackageVerifierIssue> issues)
        {
            var issuesToReport = issues.Select(issue => issueProcessor.GetIssueReport(issue, package)).ToList();

            if (issuesToReport.Count > 0)
            {
                var infos = issuesToReport.Where(issueReport => issueReport.IssueLevel == LogLevel.Info).ToList();
                var warnings = issuesToReport.Where(issueReport => issueReport.IssueLevel == LogLevel.Warning).ToList();
                var errors = issuesToReport.Where(issueReport => issueReport.IssueLevel == LogLevel.Error).ToList();

                LogLevel errorLevel = LogLevel.Info;
                if (warnings.Count > 0)
                {
                    errorLevel = LogLevel.Warning;
                }
                if (errors.Count > 0)
                {
                    errorLevel = LogLevel.Error;
                }
                logger.Log(
                    errorLevel,
                    "{0} error(s) and {1} warning(s) found with package {2} ({3})",
                    errors.Count, warnings.Count, package.Id, package.Version);

                foreach (var issueToReport in issuesToReport)
                {
                    // If requested, track ignores to assist
                    if (ignoreAssistanceMode == IgnoreAssistanceMode.ShowAll ||
                        (ignoreAssistanceMode == IgnoreAssistanceMode.ShowNew && issueToReport.IgnoreJustification == null))
                    {
                        IDictionary<string, IDictionary<string, string>> packageIgnoreInfo;
                        if (!ignoreAssistanceData.TryGetValue(package.Id, out packageIgnoreInfo))
                        {
                            packageIgnoreInfo = new Dictionary<string, IDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                            ignoreAssistanceData.Add(package.Id, packageIgnoreInfo);
                        }
                        IDictionary<string, string> packageRuleInfo;
                        if (!packageIgnoreInfo.TryGetValue(issueToReport.PackageIssue.IssueId, out packageRuleInfo))
                        {
                            packageRuleInfo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            packageIgnoreInfo.Add(issueToReport.PackageIssue.IssueId, packageRuleInfo);
                        }
                        if (packageRuleInfo.ContainsKey(issueToReport.PackageIssue.Instance ?? "*"))
                        {
                            Console.WriteLine("ALERT!!!!!!!!!!!!! Already added key {0}", issueToReport.PackageIssue.Instance);
                        }
                        else
                        {
                            packageRuleInfo.Add(issueToReport.PackageIssue.Instance ?? "*", issueToReport.IgnoreJustification ?? "Enter justification");
                        }
                    }

                    PrintPackageIssue(logger, issueToReport);
                }

                return new Tuple<int, int>(errors.Count, warnings.Count);
            }
            else
            {
                logger.LogInfo("No issues found with package {0} ({1})", package.Id, package.Version);
                return new Tuple<int, int>(0, 0);
            }
        }

        private static IList<IssueIgnore> GetIgnoresFromFile(IDictionary<string, IDictionary<string, IDictionary<string, string>>> ignoresInFile)
        {
            var issuesToIgnore = new List<IssueIgnore>();
            if (ignoresInFile != null)
            {
                foreach (var packageIgnoreData in ignoresInFile)
                {
                    var packageId = packageIgnoreData.Key;
                    foreach (var ruleIgnoreData in packageIgnoreData.Value)
                    {
                        var issueId = ruleIgnoreData.Key;
                        foreach (var instanceIgnoreData in ruleIgnoreData.Value)
                        {
                            var instance = instanceIgnoreData.Key;
                            var justification = instanceIgnoreData.Value;

                            issuesToIgnore.Add(new IssueIgnore
                            {
                                PackageId = packageId,
                                IssueId = issueId,
                                Instance = instance,
                                Justification = justification,
                            });
                        }
                    }
                }
            }

            return issuesToIgnore;
        }

        private static void PrintPackageIssue(IPackageVerifierLogger logger, IssueReport issue)
        {
            // TODO: Support this: https://confluence.jetbrains.com/display/TCD8/Build+Script+Interaction+with+TeamCity

            var issueInfo = issue.PackageIssue.IssueId;
            if (issue.PackageIssue.Instance != null)
            {
                issueInfo += " @ " + issue.PackageIssue.Instance;
            }

            logger.Log(issue.IssueLevel, "{0}: {1}", issueInfo, issue.PackageIssue.Issue);
            if (issue.IgnoreJustification != null)
            {
                logger.Log(issue.IssueLevel, "Justification: {0}", issue.IgnoreJustification);
            }
        }
    }
}
