namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private readonly Action? _exitedCallback;
    private readonly bool _displayLog;
    private readonly Action<string>? _outputSink;
    private bool _isDisposed;

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;

    // Exit code captured inside the Exited handler while the Process object is
    // still valid (reading it later after Dispose throws). Null until the process
    // has exited. Used to log WHY a core crashed (crash-recovery timeline).
    // Single writer (the Exited handler); read from the same exit callback or a
    // task scheduled off it, so no lock is needed.
    public int? ExitCode => _exitCode;
    private int? _exitCode;

    /// <summary>
    /// Wraps a child process (core binary, helper shell, etc.).
    /// </summary>
    /// <param name="outputSink">
    /// Optional sink that receives every stdout/stderr line, independent of the
    /// UI log (<c>displayLog</c>). Used to pipe core output straight into the
    /// <c>ao_diag.txt</c> file so launch crashes are visible in the field.
    /// When a sink is supplied the process output is always redirected, even if
    /// <c>displayLog</c> is false.
    /// </param>
    public ProcessService(
        string fileName,
        string arguments,
        string workingDirectory,
        bool displayLog,
        bool redirectInput,
        Dictionary<string, string>? environmentVars,
        Func<bool, string, Task>? updateFunc,
        Action? exitedCallback = null,
        Action<string>? outputSink = null)
    {
        _updateFunc = updateFunc;
        _exitedCallback = exitedCallback;
        _displayLog = displayLog;
        _outputSink = outputSink;

        // Redirect whenever we need the output for the UI log OR for the
        // diagnostic sink. Otherwise leave it unredirected so the child can
        // write straight to its console.
        var captureOutput = displayLog || outputSink != null;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = captureOutput,
                CreateNoWindow = true,
                StandardOutputEncoding = captureOutput ? Encoding.UTF8 : null,
                StandardErrorEncoding = captureOutput ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };

        if (environmentVars != null)
        {
            foreach (var kv in environmentVars)
            {
                _process.StartInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (captureOutput)
        {
            RegisterEventHandlers();
        }
    }

    public async Task StartAsync(string pwd = null)
    {
        _process.Start();

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        if (_process.StartInfo.RedirectStandardInput)
        {
            await Task.Delay(10);
            await _process.StandardInput.WriteLineAsync(pwd);
        }
    }

    public async Task StopAsync()
    {
        if (_process.HasExited)
        {
            return;
        }

        try
        {
            if (_process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }
            }

            try
            {
                if (Utils.IsNonWindows())
                {
                    _process.Kill(true);
                }
            }
            catch { }

            try
            {
                _process.Kill();
            }
            catch { }

            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            // `?.` produces a null Task when no update sink is wired; awaiting it
            // would throw NullReferenceException on top of the original error.
            if (_updateFunc != null)
            {
                await _updateFunc.Invoke(true, ex.Message);
            }
        }
    }

    private void RegisterEventHandlers()
    {
        void dataHandler(object sender, DataReceivedEventArgs e)
        {
            if (e.Data.IsNotEmpty())
            {
                if (_displayLog)
                {
                    _ = _updateFunc?.Invoke(false, e.Data + Environment.NewLine);
                }
                _outputSink?.Invoke(e.Data);
            }
        }

        _process.OutputDataReceived += dataHandler;
        _process.ErrorDataReceived += dataHandler;

        _process.Exited += (s, e) =>
        {
            try
            {
                // Capture while the Process object can still report it; a concurrent
                // Dispose would otherwise make the property throw for the caller.
                _exitCode = _process.ExitCode;
            }
            catch
            {
                // Process already disposed or access denied — leave ExitCode null.
            }

            try
            {
                _exitedCallback?.Invoke();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ProcessService", ex);
            }

            try
            {
                _process.OutputDataReceived -= dataHandler;
                _process.ErrorDataReceived -= dataHandler;
            }
            catch
            {
            }
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }

                _process.Kill();
            }

            _process.Dispose();
        }
        catch (Exception ex)
        {
            _updateFunc?.Invoke(true, ex.Message);
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
