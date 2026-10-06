using Serilog;

namespace SteamAutoCrack.Core.Utils;

public interface IProcessor
{
    public Task ProcessFileGUI(CancellationToken cancellationToken = default);
    public Task ProcessFileCLI();
}

public class Processor : IProcessor
{
    private static readonly ILogger _log = Log.ForContext<Processor>();

    public async Task ProcessFileGUI(CancellationToken cancellationToken = default)
    {
        try
        {
            _log.Information("开始处理…");
            EMUGameInfoConfig eMUGameInfoConfig = new();
            EMUConfig eMUConfigs = new();
            SteamStubUnpackerConfig steamStubUnpackerConfigs = new();
            EMUApplyConfig emuApplyConfigs = new();
            GenCrackOnlyConfig genCrackOnlyConfigs = new();
            if ((Config.Config.ProcessConfigs.Unpack || Config.Config.ProcessConfigs.ApplyEMU ||
                 Config.Config.ProcessConfigs.GenerateCrackOnly) && !File.Exists(Config.Config.InputPath) &&
                !Directory.Exists(Config.Config.InputPath)) throw new Exception("路径无效");
            if (Config.Config.ProcessConfigs.GenerateEMUGameInfo)
                eMUGameInfoConfig = Config.Config.EMUGameInfoConfigs.GetEMUGameInfoConfig();
            if (Config.Config.ProcessConfigs.GenerateEMUConfig) eMUConfigs = Config.Config.EMUConfigs.GetEMUConfig();
            if (Config.Config.ProcessConfigs.Unpack)
                steamStubUnpackerConfigs = Config.Config.SteamStubUnpackerConfigs.GetSteamStubUnpackerConfig();
            if (Config.Config.ProcessConfigs.ApplyEMU)
            {
                emuApplyConfigs = Config.Config.EMUApplyConfigs.GetEMUApplyConfig();
                if (!new EMUApply().CheckGoldberg(emuApplyConfigs))
                    throw new Exception("缺少 Goldberg 模拟器文件，请先下载 emu");
            }

            if (Config.Config.ProcessConfigs.GenerateCrackOnly)
                genCrackOnlyConfigs = Config.Config.GenCrackOnlyConfigs.GetGenCrackOnlyConfig();

            if (Config.Config.ProcessConfigs.GenerateEMUGameInfo)
            {
                _log.Information("—— 1. 生成模拟器游戏信息 ——");
                await new EMUGameInfo().Generate(eMUGameInfoConfig, cancellationToken).ConfigureAwait(false);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Config.Config.ProcessConfigs.GenerateEMUConfig)
            {
                _log.Information("—— 2. 生成模拟器配置 ——");
                new EMUConfigGenerator().Generate(eMUConfigs);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Config.Config.ProcessConfigs.Unpack)
            {
                _log.Information("—— 3. 脱 SteamStub 壳 ——");
                await new SteamStubUnpacker(steamStubUnpackerConfigs).Unpack(Config.Config.InputPath)
                    .ConfigureAwait(false);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Config.Config.ProcessConfigs.ApplyEMU)
            {
                _log.Information("—— 4. 部署 Goldberg 模拟器 ——");
                await new EMUApply().Apply(emuApplyConfigs).ConfigureAwait(false);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Config.Config.ProcessConfigs.GenerateCrackOnly)
            {
                _log.Information("—— 5. 生成纯破解包 ——");
                await new GenCrackOnly().Applier(genCrackOnlyConfigs).ConfigureAwait(false);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (Config.Config.ProcessConfigs.Restore)
            {
                _log.Information("—— 6. 还原 ——");
                await new Restore().RestoreFile(Config.Config.InputPath).ConfigureAwait(false);
                
            }

            cancellationToken.ThrowIfCancellationRequested();

            _log.Information("全部流程完成");
            GC.Collect();
        }
        catch (OperationCanceledException)
        {
            _log.Information("操作已取消");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "处理失败");
        }
    }

    public async Task ProcessFileCLI()
    {
        try
        {
            _log.Information("开始处理…");
            EMUGameInfoConfig eMUGameInfoConfig = new();
            EMUConfig eMUConfigs = new();
            SteamStubUnpackerConfig steamStubUnpackerConfigs = new();
            EMUApplyConfig emuApplyConfigs = new();
            GenCrackOnlyConfig genCrackOnlyConfigs = new();
            if ((Config.Config.ProcessConfigs.Unpack || Config.Config.ProcessConfigs.ApplyEMU ||
                 Config.Config.ProcessConfigs.GenerateCrackOnly) && !File.Exists(Config.Config.InputPath) &&
                !Directory.Exists(Config.Config.InputPath)) throw new Exception("路径无效");
            if (Config.Config.ProcessConfigs.GenerateEMUGameInfo)
                eMUGameInfoConfig = Config.Config.EMUGameInfoConfigs.GetEMUGameInfoConfig();
            if (Config.Config.ProcessConfigs.GenerateEMUConfig) eMUConfigs = Config.Config.EMUConfigs.GetEMUConfig();
            if (Config.Config.ProcessConfigs.Unpack)
                steamStubUnpackerConfigs = Config.Config.SteamStubUnpackerConfigs.GetSteamStubUnpackerConfig();
            if (Config.Config.ProcessConfigs.ApplyEMU)
            {
                emuApplyConfigs = Config.Config.EMUApplyConfigs.GetEMUApplyConfig();
                if (!new EMUApply().CheckGoldberg(emuApplyConfigs))
                    throw new Exception("缺少 Goldberg 模拟器文件，请先下载 emu");
            }

            if (Config.Config.ProcessConfigs.GenerateCrackOnly)
                genCrackOnlyConfigs = Config.Config.GenCrackOnlyConfigs.GetGenCrackOnlyConfig();

            if (Config.Config.ProcessConfigs.GenerateEMUGameInfo)
            {
                _log.Information("—— 1. 生成模拟器游戏信息 ——");
                await new EMUGameInfo().Generate(eMUGameInfoConfig).ConfigureAwait(false);
                
            }

            if (Config.Config.ProcessConfigs.GenerateEMUConfig)
            {
                _log.Information("—— 2. 生成模拟器配置 ——");
                new EMUConfigGenerator().Generate(eMUConfigs);
                
            }

            if (Config.Config.ProcessConfigs.Unpack)
            {
                _log.Information("—— 3. 脱 SteamStub 壳 ——");
                await new SteamStubUnpacker(steamStubUnpackerConfigs).Unpack(Config.Config.InputPath)
                    .ConfigureAwait(false);
                
            }

            if (Config.Config.ProcessConfigs.ApplyEMU)
            {
                _log.Information("—— 4. 部署 Goldberg 模拟器 ——");
                await new EMUApply().Apply(emuApplyConfigs).ConfigureAwait(false);
                
            }

            if (Config.Config.ProcessConfigs.GenerateCrackOnly)
            {
                _log.Information("—— 5. 生成纯破解包 ——");
                await new GenCrackOnly().Applier(genCrackOnlyConfigs).ConfigureAwait(false);
                
            }

            if (Config.Config.ProcessConfigs.Restore)
            {
                _log.Information("—— 6. 还原 ——");
                await new Restore().RestoreFile(Config.Config.InputPath).ConfigureAwait(false);
                
            }

            _log.Information("全部流程完成");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "处理失败");
        }
    }
}