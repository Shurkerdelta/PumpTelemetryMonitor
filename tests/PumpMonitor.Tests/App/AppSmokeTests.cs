using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PumpMonitor.Tests.App;

/// <summary>
/// Runs the real console app with redirected input and output, as a piped script would, to check that startup,
/// commands, alert printing and graceful shutdown work together. Everything below it is unit tested; this
/// catches wiring mistakes in Program.cs.
/// </summary>
public class AppSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Commands_Status_Fault_AndQuit_WorkEndToEnd()
    {
        await using var app = AppProcess.Start();

        string started = await app.WaitForLineAsync(l => l.StartsWith("Started "));
        Match devices = Regex.Match(started, @"^Started (\d) devices: (pump-\d(, )?)+$");
        Assert.True(devices.Success, started);
        Assert.InRange(int.Parse(devices.Groups[1].Value), 3, 5);

        await app.SendAsync("status");
        await app.WaitForLineAsync(l => l.Trim() == "pump-1: ON");
        await app.WaitForLineAsync(l => l == "Alert thresholds:");

        await app.SendAsync("fault spike pump-1");
        await app.WaitForLineAsync(l => l == "Injected spike: pump-1");
        string alert = await app.WaitForLineAsync(l => l.StartsWith("!! ALERT"));
        Assert.Contains("pump-1", alert);
        Assert.Contains("HighPressure", alert);

        await app.SendAsync("quit");
        await app.WaitForLineAsync(l => l == "All devices stopped.");
        Assert.Equal(0, await app.WaitForExitAsync());
        Assert.Equal("", app.StandardError);
    }

    [Fact]
    public async Task EndOfInput_ShutsDownGracefully_AndFlagsShowReadingsAndUnknownArgs()
    {
        await using var app = AppProcess.Start("-v", "--bogus");

        await app.WaitForLineAsync(l => l.StartsWith("Printing every reading."));
        string reading = await app.WaitForLineAsync(l => l.Contains(" P=") && l.Contains(" T="));
        Assert.Matches(@"pump-\d\s+#\d+\s+P=", reading);

        app.CloseInput();
        await app.WaitForLineAsync(l => l == "All devices stopped.");
        Assert.Equal(0, await app.WaitForExitAsync());
        Assert.Contains("Ignoring unknown arguments: --bogus", app.StandardError);
    }

    private sealed class AppProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly StringBuilder _stderr = new();
        private readonly List<string> _seen = [];

        private AppProcess(Process process) => _process = process;

        public string StandardError
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

        public static AppProcess Start(params string[] args)
        {
            // `dotnet test` exposes the host it runs under; fall back to the one on PATH.
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PumpMonitor.App.dll"));
            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            var process = new Process { StartInfo = info };
            var app = new AppProcess(process);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    app._lines.Writer.TryComplete();
                }
                else
                {
                    app._lines.Writer.TryWrite(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (app._stderr)
                    {
                        app._stderr.AppendLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return app;
        }

        public async Task SendAsync(string command)
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
        }

        public void CloseInput() => _process.StandardInput.Close();

        /// <summary>Reads output until a line matches, failing with everything seen if none does in time.</summary>
        public async Task<string> WaitForLineAsync(Func<string, bool> match)
        {
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                while (await _lines.Reader.WaitToReadAsync(cts.Token))
                {
                    while (_lines.Reader.TryRead(out string? line))
                    {
                        _seen.Add(line);
                        if (match(line))
                        {
                            return line;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }

            Assert.Fail($"Expected line not printed. Output:{Environment.NewLine}{string.Join(Environment.NewLine, _seen)}" +
                $"{Environment.NewLine}Stderr:{Environment.NewLine}{StandardError}");
            return "";
        }

        public async Task<int> WaitForExitAsync()
        {
            await _process.WaitForExitAsync().WaitAsync(Timeout);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }
    }
}
