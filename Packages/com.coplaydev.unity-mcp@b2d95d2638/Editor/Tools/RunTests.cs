using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Resources.Tests;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Starts a Unity Test Runner run asynchronously and returns a job id immediately.
    /// Use get_test_job(job_id) to poll status/results.
    /// </summary>
    [McpForUnityTool("run_tests", AutoRegister = false, Group = "testing")]
    public static class RunTests
    {
        public static Task<object> HandleCommand(JObject @params)
        {
            try
            {
                // Check for clear_stuck action first
                if (ParamCoercion.CoerceBool(@params?["clear_stuck"], false))
                {
                    bool wasCleared = TestJobManager.ClearStuckJob();
                    return Task.FromResult<object>(new SuccessResponse(
                        wasCleared ? "Stuck job cleared." : "No running job to clear.",
                        new { cleared = wasCleared }
                    ));
                }

                string modeStr = @params?["mode"]?.ToString();
                if (string.IsNullOrWhiteSpace(modeStr))
                {
                    modeStr = "EditMode";
                }

                if (!ModeParser.TryParse(modeStr, out var parsedMode, out var parseError))
                {
                    return Task.FromResult<object>(new ErrorResponse(parseError));
                }

                var p = new ToolParams(@params);
                bool includeDetails = p.GetBool("includeDetails");
                bool includeFailedTests = p.GetBool("includeFailedTests");

                McpLog.Info($"[RunTests] Raw params: {(@params != null ? @params.ToString() : "<null>")}");
                return HandleRunAsync(@params, parsedMode.Value, includeDetails, includeFailedTests);
            }
            catch (Exception ex)
            {
                // Normalize the already-running case to a stable error token.
                if (ex.Message != null && ex.Message.IndexOf("already in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Task.FromResult<object>(new ErrorResponse("tests_running", new { reason = "tests_running", retry_after_ms = 5000 }));
                }
                return Task.FromResult<object>(new ErrorResponse($"Failed to start test job: {ex.Message}"));
            }
        }

        private static async Task<object> HandleRunAsync(JObject @params, TestMode parsedMode, bool includeDetails, bool includeFailedTests)
        {
            var filterOptions = GetFilterOptions(@params);
            filterOptions = await ResolveTestFiltersAsync(parsedMode, filterOptions).ConfigureAwait(true);
            McpLog.Info(
                $"[RunTests] Parsed request mode={parsedMode} includeDetails={includeDetails} includeFailedTests={includeFailedTests} " +
                $"filters={DescribeFilters(filterOptions)}");
            string jobId = TestJobManager.StartJob(parsedMode, filterOptions);

            return new SuccessResponse("Test job started.", new
            {
                job_id = jobId,
                status = "running",
                mode = parsedMode.ToString(),
                include_details = includeDetails,
                include_failed_tests = includeFailedTests
            });
        }

        private static TestFilterOptions GetFilterOptions(JObject @params)
        {
            if (@params == null)
            {
                return null;
            }

            var p = new ToolParams(@params);
            var testNames = NormalizeFilterValues("testNames", p.GetStringArray("testNames"));
            var groupNames = NormalizeFilterValues("groupNames", p.GetStringArray("groupNames"));
            var categoryNames = NormalizeFilterValues("categoryNames", p.GetStringArray("categoryNames"));
            var assemblyNames = NormalizeFilterValues("assemblyNames", p.GetStringArray("assemblyNames"));

            McpLog.Info(
                $"[RunTests] Normalized filters " +
                $"testNames={DescribeArray(testNames)} " +
                $"groupNames={DescribeArray(groupNames)} " +
                $"categoryNames={DescribeArray(categoryNames)} " +
                $"assemblyNames={DescribeArray(assemblyNames)}");

            if (testNames == null && groupNames == null && categoryNames == null && assemblyNames == null)
            {
                return null;
            }

            return new TestFilterOptions
            {
                TestNames = testNames,
                GroupNames = groupNames,
                CategoryNames = categoryNames,
                AssemblyNames = assemblyNames
            };
        }

        private static string[] NormalizeFilterValues(string label, string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return null;
            }

            var normalized = values
                .SelectMany(SplitFilterValue)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (!Enumerable.SequenceEqual(values, normalized, StringComparer.Ordinal))
            {
                McpLog.Info(
                    $"[RunTests] Expanded {label} raw={DescribeArray(values)} " +
                    $"normalized={DescribeArray(normalized)}");
            }

            return normalized.Length > 0 ? normalized : null;
        }

        private static IEnumerable<string> SplitFilterValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var trimmed = value.Trim();
            if (trimmed.IndexOf(',') < 0)
            {
                yield return trimmed;
                yield break;
            }

            foreach (var part in trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = part.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static string DescribeFilters(TestFilterOptions filterOptions)
        {
            if (filterOptions == null)
            {
                return "<none>";
            }

            return
                $"testNames={DescribeArray(filterOptions.TestNames)} " +
                $"groupNames={DescribeArray(filterOptions.GroupNames)} " +
                $"categoryNames={DescribeArray(filterOptions.CategoryNames)} " +
                $"assemblyNames={DescribeArray(filterOptions.AssemblyNames)}";
        }

        private static string DescribeArray(string[] values)
        {
            return values == null ? "<null>" : $"[{string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)))}]";
        }

        private static async Task<TestFilterOptions> ResolveTestFiltersAsync(TestMode mode, TestFilterOptions filterOptions)
        {
            if (filterOptions?.TestNames == null || filterOptions.TestNames.Length == 0)
            {
                return filterOptions;
            }

            IReadOnlyList<System.Collections.Generic.Dictionary<string, string>> discoveredTests = null;
            try
            {
                discoveredTests = await MCPServiceLocator.Tests.GetTestsAsync(mode).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[RunTests] Failed to resolve test names before execution: {ex.Message}");
                return filterOptions;
            }

            if (discoveredTests == null || discoveredTests.Count == 0)
            {
                McpLog.Warn("[RunTests] Test discovery returned no tests while resolving requested test names.");
                return filterOptions;
            }

            McpLog.Info($"[RunTests] Test discovery returned {discoveredTests.Count} entries for mode={mode}.");

            var resolved = filterOptions.TestNames
                .Select(requested => ResolveSingleTestName(requested, filterOptions.AssemblyNames, discoveredTests))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            McpLog.Info(
                $"[RunTests] Resolved test names requested={DescribeArray(filterOptions.TestNames)} " +
                $"resolved={DescribeArray(resolved)}");

            return new TestFilterOptions
            {
                TestNames = resolved.Length > 0 ? resolved : filterOptions.TestNames,
                GroupNames = filterOptions.GroupNames,
                CategoryNames = filterOptions.CategoryNames,
                AssemblyNames = filterOptions.AssemblyNames
            };
        }

        private static string ResolveSingleTestName(
            string requestedName,
            string[] assemblyNames,
            IReadOnlyList<System.Collections.Generic.Dictionary<string, string>> discoveredTests)
        {
            if (string.IsNullOrWhiteSpace(requestedName))
            {
                return requestedName;
            }

            var candidates = discoveredTests.Where(t => MatchesAssemblyFilter(t, assemblyNames)).ToList();
            if (candidates.Count == 0)
            {
                candidates = discoveredTests.ToList();
            }

            string resolved = TryResolveExact(candidates, requestedName, "full_name")
                ?? TryResolveExact(candidates, requestedName, "name")
                ?? TryResolveSuffix(candidates, requestedName)
                ?? requestedName;

            if (!string.Equals(resolved, requestedName, StringComparison.Ordinal))
            {
                McpLog.Info($"[RunTests] Resolved requested test '{requestedName}' to '{resolved}'.");
            }
            else
            {
                McpLog.Info($"[RunTests] Could not refine requested test '{requestedName}'; using original value.");
            }

            return resolved;
        }

        private static bool MatchesAssemblyFilter(System.Collections.Generic.Dictionary<string, string> test, string[] assemblyNames)
        {
            if (assemblyNames == null || assemblyNames.Length == 0)
            {
                return true;
            }

            test.TryGetValue("path", out var path);
            path = path ?? string.Empty;

            return assemblyNames.Any(assembly =>
                !string.IsNullOrWhiteSpace(assembly) &&
                (path.IndexOf(assembly, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 path.IndexOf($"{assembly}.dll", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string TryResolveExact(
            System.Collections.Generic.IEnumerable<System.Collections.Generic.Dictionary<string, string>> tests,
            string requestedName,
            string key)
        {
            var matches = tests
                .Where(t => t.TryGetValue(key, out var value) && string.Equals(value, requestedName, StringComparison.Ordinal))
                .Select(t => t.TryGetValue("full_name", out var fullName) ? fullName : requestedName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return matches.Length == 1 ? matches[0] : null;
        }

        private static string TryResolveSuffix(
            System.Collections.Generic.IEnumerable<System.Collections.Generic.Dictionary<string, string>> tests,
            string requestedName)
        {
            var matches = tests
                .Where(t => t.TryGetValue("full_name", out var fullName) &&
                            (fullName.EndsWith("." + requestedName, StringComparison.Ordinal) ||
                             fullName.EndsWith(requestedName, StringComparison.Ordinal)))
                .Select(t => t["full_name"])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return matches.Length == 1 ? matches[0] : null;
        }
    }
}
