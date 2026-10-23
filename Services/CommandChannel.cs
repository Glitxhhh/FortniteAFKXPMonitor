using System.IO;
using System.IO.Pipes;

namespace FortniteAFKXPMonitor.Services;

/// <summary>
/// Lets another process drive the running app: <c>FortniteAFKXPMonitor --toggle</c> (also --start, --stop, --status).
///
/// Why this exists: on Wayland the global hotkey is read straight from /dev/input, which only sees physical
/// keyboards. Input forwarded by Deskflow / Synergy / Input Leap arrives through libei, i.e. it goes into the
/// compositor and never appears as a kernel input device, so that hotkey can't see it. The compositor *does*
/// see it, so binding "FortniteAFKXPMonitor --toggle" to a key in the desktop's own shortcut settings works for
/// any input source.
/// </summary>
public static class CommandChannel
{
    public static readonly string[] Commands = { "toggle", "start", "stop", "status" };

    // Per-user, so two logged-in users don't talk to each other's copy.
    private static string PipeName => "FortniteAFKXPMonitor-" + Environment.UserName;

    /// <summary>True when <paramref name="args"/> is exactly one of the --toggle / --start / --stop / --status flags.</summary>
    public static bool TryParse(string[] args, out string command)
    {
        command = "";
        if (args.Length != 1 || !args[0].StartsWith("--", StringComparison.Ordinal))
            return false;

        string name = args[0][2..].ToLowerInvariant();
        if (!Commands.Contains(name))
            return false;

        command = name;
        return true;
    }

    /// <summary>
    /// Sends the command to the running app and prints its reply ("running" / "stopped").
    /// Exit code: 0 ok (for --status: running), 1 (--status: stopped), 2 the app isn't running.
    /// </summary>
    public static int RunClient(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(1500);

            var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);

            string reply = new StreamReader(client).ReadLine() ?? "";
            Console.WriteLine(reply);
            return command == "status" && reply != "running" ? 1 : 0;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("FortniteAFKXPMonitor isn't running.");
            return 2;
        }
    }

    /// <summary>Listens for commands from <see cref="RunClient"/> and hands them to a handler.</summary>
    public sealed class Server : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Func<string, Task<string>> _handle;

        /// <param name="handle">Receives a command from <see cref="Commands"/> and returns the reply text.</param>
        public Server(Func<string, Task<string>> handle)
        {
            _handle = handle;
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return; // another copy of the app already owns the channel; it handles the commands
                }

                try
                {
                    using (pipe)
                    {
                        await pipe.WaitForConnectionAsync(_cts.Token);

                        // A client that connects and goes quiet mustn't wedge the server.
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));

                        string? line = await new StreamReader(pipe).ReadLineAsync(timeout.Token);
                        string command = line?.Trim().ToLowerInvariant() ?? "";
                        string reply = Commands.Contains(command) ? await _handle(command) : "error: unknown command";

                        var writer = new StreamWriter(pipe) { AutoFlush = true };
                        await writer.WriteLineAsync(reply.AsMemory(), timeout.Token);
                        if (OperatingSystem.IsWindows())
                            pipe.WaitForPipeDrain();
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // client went away or timed out: just wait for the next one
                }
            }
        }

        public void Dispose() => _cts.Cancel();
    }
}
