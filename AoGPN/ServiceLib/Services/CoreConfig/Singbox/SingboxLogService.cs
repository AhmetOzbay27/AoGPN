namespace ServiceLib.Services.CoreConfig;

public partial class CoreConfigSingboxService
{
    private void GenLog()
    {
        try
        {
            var level = _config.CoreBasicItem.Loglevel;
            var masterLogOff = !_config.GuiItem.EnableLog;
            var verboseDiag = _config.GuiItem.EnableVerboseLog;

            // Honor the master log switch and the "none" level: no core log file at all.
            if (masterLogOff || level == Global.None)
            {
                _coreConfig.log.disabled = true;
                return;
            }

            // Map the UI level to a sing-box level. The ao_singbox file is a
            // diagnostic artifact (TUN creation errors, process_name match
            // failures, routing decisions). Without verbose diagnostics it is
            // clamped to "warn": a "debug"/"info" setting previously flooded
            // the disk with ~500K router/DNS rows per day (observed: 55 MB / 520K
            // lines), even though only warn+ rows matter for field diagnostics.
            // With verbose diagnostics enabled the user's exact level is honored.
            var effective = level switch
            {
                "debug" or "info" when verboseDiag => level,
                "debug" or "info" => "warn",
                "warning" => "warn",
                "error" => "error",
                _ => "warn",
            };
            _coreConfig.log.level = effective;

            var dtNow = DateTime.Now;
            try
            {
                _coreConfig.log.output = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(DiagLog.FilePath) ?? ".",
                    $"ao_singbox_{dtNow:yyyy-MM-dd}.log");
                DiagLog.Write($"LOG singbox output set to: {_coreConfig.log.output} (level={effective})");
            }
            catch
            {
                _coreConfig.log.output = Utils.GetLogPath($"sbox_{dtNow:yyyy-MM-dd}.txt");
            }

            // GPN + WARP oturumlarında access-log kanıtı (docs/gpn-warp-live-test.md
            // sözleşmesi): "outbound/warp" vs "outbound/proxy" satırları yalnızca
            // info/debug seviyesinde yazılır. Kullanıcı verbose açmadıysa seviye
            // kasıtlı olarak info'ya ZORLANMAZ (disk seli kaçınma politikası korunur),
            // ama durum GPN oturum günlüğüne açıkça düşer — test öncesi bu tek satırdan
            // access-line'ların açık olup olmadığı + tüm log dosya yolları doğrulanır.
            if (_node is { ConfigType: EConfigType.WireGuard } && RoutingNeedsWarpOutbound())
            {
                var disabled = _coreConfig.log.disabled == true;
                var armed = !disabled && (effective is "info" or "debug");
                DiagLog.Write($"GPN_LOG warp-access armed={(armed ? "YES" : "NO")} "
                    + $"reason={(disabled ? "core-log-off" : armed ? "ok" : "level-warn-verbose-off")} "
                    + $"level={effective} verbose={verboseDiag} "
                    + $"diag={DiagLog.FilePath} gpn-session={GpnSessionLog.FilePath} "
                    + $"access={_coreConfig.log.output ?? "?"}");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }
}
