using LLMHostProject.Core;

// LLMHost.Updater: started by the app with the arguments below, it waits for the app to close, replaces the installation, starts the program
// again and puts the old files back when that fails. Its log is %TEMP%\llmhost-update.log. Exit code: 0 updated, 1 failed and restored, 2 failed.
//   --target <folder> --package <file> --kind zip|msi|exe --pid <n> [--before "<command>"] [--after "<command>"] [--version <v>]
string? Arg(string name) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string log = Path.Combine(Path.GetTempPath(), "llmhost-update.log");
void Log(string m) { try { File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {m}{Environment.NewLine}"); } catch { } }

string? target = Arg("target"), package = Arg("package"), kind = Arg("kind");
if (target == null || package == null || kind == null) { Log("Missing arguments."); return 2; }
var plan = new UpdatePlan(target, package, kind, int.TryParse(Arg("pid"), out var pid) ? pid : 0, Arg("before"), Arg("after"), Arg("version") ?? "");
var result = UpdateApply.Apply(plan, new ProcessUpdateEnv(Log));
Log($"{result.Outcome}: {result.Message}");
return result.Outcome switch { UpdateOutcome.Updated => 0, UpdateOutcome.FailedRolledBack => 1, _ => 2 };
