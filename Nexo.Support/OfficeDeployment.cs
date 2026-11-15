using System.IO;
namespace Nexo.Support;

public enum OfficeDeploymentState { Declined, Blocked, Cancelled, Completed, RestartRequired, Failed, Unknown }
public sealed record OfficePreflight(bool CanInstall, IReadOnlyList<string> Blockers);
public sealed record OfficeDeploymentResult(OfficeDeploymentState State, string Message, int? ExitCode = null);
public sealed record OfficeDeploymentProgress(string Stage, string Message);
public interface IOfficeDeploymentPlatform
{
    bool InstallationInProgress => false;
    Task<OfficePreflight> CheckAsync(CancellationToken token);
    Task DownloadAsync(string destination, CancellationToken token);
    Task<bool> IsMicrosoftSignedAsync(string path, CancellationToken token);
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token);
    Task<OfficeInstallationVerification> VerifyInstallationAsync(CancellationToken token);
}

/// <summary>Closed deployment recipe. Call only after explicit installation and license acknowledgement.</summary>
public sealed class OfficeDeploymentService(IOfficeDeploymentPlatform platform)
{
    public const string LicenseNotice = "Instalar Office LTSC Professional Plus 2024, 64 bits, português (Brasil). A licença por volume e a ativação são responsabilidade da TI. O Nexo não fornece licença nem ativa o Office. Salve seus documentos e feche os aplicativos Office antes de continuar.";
    public const string Configuration = """
        <Configuration>
          <Add OfficeClientEdition="64" Channel="PerpetualVL2024"><Product ID="ProPlus2024Volume"><Language ID="pt-br" /></Product></Add>
          <Display Level="None" AcceptEULA="TRUE" />
          <Property Name="AUTOACTIVATE" Value="0" />
          <Property Name="SETUP_REBOOT" Value="Never" />
        </Configuration>
        """;
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<OfficePreflight> CheckAsync(CancellationToken cancellationToken = default) => platform.CheckAsync(cancellationToken);
    public async Task<OfficeDeploymentResult> InstallAsync(bool installationAndLicenseAcknowledged,
        IProgress<OfficeDeploymentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!installationAndLicenseAcknowledged) return new(OfficeDeploymentState.Declined, "Instalação não confirmada.");
        var entered = false; var configuring = false; var verifying = false; string? folder = null;
        void Report(string stage, string message) => progress?.Report(new(stage, message));
        try
        {
            await gate.WaitAsync(cancellationToken); entered = true;
            if (platform.InstallationInProgress) return new(OfficeDeploymentState.Blocked, "Uma instalação Office ainda está em andamento. Aguarde seu término antes de tentar novamente.");
            Report("preflight", "Verificando requisitos e conflitos do Office.");
            var check = await platform.CheckAsync(cancellationToken);
            if (!check.CanInstall) return new(OfficeDeploymentState.Blocked, string.Join("; ", check.Blockers));
            folder = Path.Combine(Path.GetTempPath(), "NexoOffice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var extractor = Path.Combine(folder, "odt.exe");
            Report("download", "Baixando Office Deployment Tool da Microsoft.");
            await platform.DownloadAsync(extractor, cancellationToken);
            if (!await platform.IsMicrosoftSignedAsync(extractor, cancellationToken)) throw new InvalidDataException("Assinatura Microsoft do ODT não validada.");
            // Recheck immediately before mutation; downloading may take long enough for Office to open.
            check = await platform.CheckAsync(cancellationToken);
            if (!check.CanInstall) return new(OfficeDeploymentState.Blocked, string.Join("; ", check.Blockers));
            cancellationToken.ThrowIfCancellationRequested();
            Report("install", "Instalando Office pela Microsoft. O Nexo não fecha aplicativos nem reinicia o computador.");
            configuring = true;
            var code = await platform.RunAsync(extractor, ["/deploy-office"], cancellationToken);
            configuring = false;
            if (code == 0)
            {
                verifying = true;
                Report("verify", "Conferindo a edição, arquitetura, idioma e aplicativos pelo Windows/Click-to-Run.");
                var verification = await platform.VerifyInstallationAsync(cancellationToken);
                verifying = false;
                return new(verification.Verified ? OfficeDeploymentState.Completed : OfficeDeploymentState.Unknown, verification.Detail, code);
            }
            return code switch
            {
                3010 => new(OfficeDeploymentState.RestartRequired, "Instalador pediu reinicialização manual. Salve seus arquivos.", code),
                60002 => new(OfficeDeploymentState.Unknown, "O executor não confirmou o término do Office. A instalação pode continuar; consulte a TI antes de tentar novamente.", code),
                < 0 => new(OfficeDeploymentState.Unknown, "O executor do Office terminou inesperadamente. Consulte a TI antes de tentar novamente.", code),
                _ => new(OfficeDeploymentState.Failed, "O instalador do Office retornou erro " + code + ". Consulte a TI.", code)
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            configuring = false;
            return new(OfficeDeploymentState.Declined, "A autorização do Windows (UAC) foi recusada. A instalação não foi iniciada.");
        }
        catch (OperationCanceledException)
        {
            return configuring || verifying ? new(OfficeDeploymentState.Unknown, "A espera ou verificação foi cancelada. O resultado do Office não foi confirmado; verifique com a TI antes de tentar novamente.")
                : new(OfficeDeploymentState.Cancelled, "Operação cancelada antes de iniciar a instalação.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(configuring || verifying ? OfficeDeploymentState.Unknown : OfficeDeploymentState.Failed,
                configuring || verifying ? "Não foi possível confirmar o resultado do instalador. Verifique com a TI antes de tentar novamente." : PreparationFailure(ex));
        }
        finally
        {
            // Never remove a working directory that a detached installer may still be using.
            if (folder != null && !configuring) { try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (entered) gate.Release();
        }
    }
    private static string PreparationFailure(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => "Não foi possível acessar a Microsoft. Verifique a rede, o proxy e as permissões de acesso à internet.",
        UnauthorizedAccessException => "Permissão insuficiente para preparar o Office. Consulte a TI e autorize o UAC quando solicitado.",
        IOException io when (io.HResult & 0xffff) is 112 or 39 => "Espaço insuficiente para preparar o Office. Libere espaço e tente novamente.",
        _ => "Preparação do Office falhou: " + ex.Message
    };
}
