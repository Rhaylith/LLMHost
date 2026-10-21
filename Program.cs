using LLMHostProject.Cli;
using LLMHostProject.Core;
using LLMHostProject.Desktop;

namespace LLMHostProject;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Ui.EnableAnsi();
        using var host = new AppHost();
        var shell = new Shell(host);

        // Default: host the web UI (and the API) from this executable in its own native window.
        if (args.Length == 0 || (args[0].StartsWith("--") && args[0] is not "--help"))
            return await RunDesktopAsync(shell, args);

        string cmd = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        switch (cmd)
        {
            case "-h": case "--help": case "help":
                await shell.DispatchAsync("help", rest);
                return 0;

            case "ui": case "window": case "desktop":
                return await RunDesktopAsync(shell, rest);

            case "cli": case "shell": case "repl":
                // interactive terminal session (optionally loading a model first)
                return await shell.RunInteractiveAsync(rest.FirstOrDefault());

            case "server" when rest.Length > 0 && rest[0] is "start" or "stop" or "status":
                return await shell.RunCommandAsync("server", rest);

            case "serve": case "server":
            {
                // headless: API + web UI over HTTP only, no window
                int rc = await shell.RunCommandAsync("serve", rest);
                if (rc != 0) return rc;
                await shell.WaitForShutdownAsync();
                return 0;
            }

            case "chat":
            {
                // llmhost chat [model] [load options]   -> load locally, then the interactive session
                // llmhost chat --server <url> [--token t] [model]   or, with a daemon running and no local load options,
                // a chat against that server
                var a = new Args(rest);
                string? serverUrl = a.Value("server");
                string? token = a.Value("token");
                if (serverUrl == null && !a.Flag("local"))
                {
                    var d = await Daemon.ProbeAsync();
                    if (d != null && a.Positional.Count <= 1 && !a.Flag("cpu") && a.Value("ctx") == null && a.Value("gpu-layers") == null)
                    { serverUrl = d.Value.Info.Url; token ??= d.Value.Info.Token; }
                }
                if (serverUrl != null)
                {
                    if (token == null && Daemon.Read() is { } di && di.Url.TrimEnd('/') == serverUrl.TrimEnd('/')) token = di.Token;
                    return await shell.RemoteChatAsync(serverUrl, token, a.Positional.FirstOrDefault());
                }
                if (a.Positional.Count == 0) return await shell.RunInteractiveAsync();
                int rc = await shell.RunCommandAsync("load", rest);
                if (rc != 0) return rc;
                return await shell.RunInteractiveAsync();
            }

            default:
                return await shell.RunCommandAsync(cmd, rest);
        }
    }

    /// <summary>
    /// Desktop mode: one window per user (a second launch just brings the first one back), served either by a
    /// background daemon that is already running or by this process.
    /// </summary>
    private static async Task<int> RunDesktopAsync(Shell shell, string[] args)
    {
        // --no-window / --headless / --no-browser keep a server-only mode; --minimized starts in the tray
        bool headless = args.Any(a => a is "--no-window" or "--headless" or "--no-browser");
        bool minimized = args.Contains("--minimized");
        var serveArgs = args.Where(a => a is not ("--no-window" or "--headless" or "--no-browser" or "--minimized")).ToArray();

        if (!headless && !DesktopHost.AcquireSingleInstance()) return 0;       // the running instance shows itself

        string url;
        var daemon = serveArgs.Length == 0 ? await Daemon.ProbeAsync() : null;
        if (daemon != null)
        {
            // a background server is already up: this window is just a client of it
            var di = daemon.Value.Info;
            url = di.Url + (string.IsNullOrEmpty(di.Token) ? "" : "/?k=" + Uri.EscapeDataString(di.Token));
            Ui.Info(Ui.Dim($"Using the daemon at {di.Url}"));
        }
        else
        {
            int rc = await shell.RunCommandAsync("serve", serveArgs);
            if (rc != 0) return rc;
            url = shell.UiUrl ?? shell.ServerUrl!;
        }

        if (headless)
        {
            await shell.WaitForShutdownAsync();
            return 0;
        }

        if (!DesktopHost.IsWebViewAvailable(out var detail))
        {
            Ui.Error("The Microsoft Edge WebView2 Runtime is not installed, so the window cannot be shown " +
                     $"({detail}). Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703.");
            Ui.Info($"The server is running at {shell.ServerUrl}; press Ctrl+C to stop.");
            await shell.WaitForShutdownAsync();
            return 1;
        }

        DesktopHost.HideOwnedConsole();
        await DesktopHost.RunWindowAsync(url, minimized);
        if (daemon == null) await shell.StopServerAsync();
        return 0;
    }
}
