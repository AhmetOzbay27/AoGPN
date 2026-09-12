namespace ServiceLib.Common;

public static class ProcUtils
{
    private static readonly string _tag = "ProcUtils";

    public static void ProcessStart(string? fileName, string arguments = "")
    {
        _ = ProcessStart(fileName, arguments, null);
    }

    public static int? ProcessStart(string? fileName, string arguments, string? dir)
    {
        if (fileName.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            if (fileName.Contains(' '))
            {
                fileName = fileName.AppendQuotes();
            }
            if (arguments.Contains(' '))
            {
                arguments = arguments.AppendQuotes();
            }

            Process proc = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = dir ?? string.Empty
                }
            };
            _ = proc.Start();
            return dir is null ? null : proc.Id;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        return null;
    }

    /// <summary>
    /// Uygulamayı aynı yetkiyle yeniden başlatır (yükseltme istemez). Windows
    /// dışındaki platformlarda yapılandırma geri yükleme sonrası yeniden başlatma
    /// için kullanılır; yönetici yükseltmesi gereken Windows yolu
    /// <see cref="RebootAsAdmin"/> üzerinden yürür.
    /// </summary>
    /// <returns>Başlatıldıysa true.</returns>
    public static bool RestartApplication()
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                UseShellExecute = true,
                WorkingDirectory = Utils.StartupPath(),
                FileName = Utils.GetExePath().AppendQuotes(),
            };
            return Process.Start(startInfo) != null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    public static bool RebootAsAdmin(bool blAdmin = true)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                UseShellExecute = true,
                Arguments = Global.RebootAs,
                WorkingDirectory = Utils.StartupPath(),
                FileName = Utils.GetExePath().AppendQuotes(),
                Verb = blAdmin ? "runas" : null,
            };
            return Process.Start(startInfo) != null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }
}
