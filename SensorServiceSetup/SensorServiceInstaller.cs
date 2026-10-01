using System.IO;

namespace DevOverlay.SensorServiceSetup;

public sealed record ServiceRegistration(string? ImagePath);

/// <summary>Service Control Manager operations used by Install/Repair/Uninstall; Stop must wait until stopped.</summary>
public interface IServiceControlManager
{
    ServiceRegistration? Query();
    void Stop();
    void Create(string quotedImagePath);
    void Reconfigure(string quotedImagePath);
    void Disable();
    void Start();
    void Delete();
}

public sealed class SensorServiceSetupException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The LocalSystem service is only ever registered against the protected copy under Program Files.
/// The portable package folder is used as a copy source and never as the service ImagePath.
/// </summary>
public sealed class SensorServiceInstaller(
    SensorServiceInstallLayout layout, IServiceControlManager serviceControl, IServiceDirectorySecurity security)
{
    private readonly SensorServicePayload _payload = new(layout);

    public void Install(string sourceDirectory)
    {
        var source = _payload.ValidateSource(sourceDirectory);
        if (serviceControl.Query() is not null)
            throw new SensorServiceSetupException("Sensor Service is already installed; choose Repair.");
        try
        {
            Deploy(source);
            serviceControl.Create(layout.QuotedExecutablePath);
        }
        catch
        {
            _payload.TryRemoveAll();
            throw;
        }
        serviceControl.Start();
        _payload.TryRemovePrevious();
    }

    public void Repair(string sourceDirectory)
    {
        var source = _payload.ValidateSource(sourceDirectory);
        if (serviceControl.Query() is null)
            throw new SensorServiceSetupException("Sensor Service is not installed; choose Install.");
        serviceControl.Stop();
        try
        {
            Deploy(source);
            serviceControl.Reconfigure(layout.QuotedExecutablePath);
        }
        catch (Exception exception)
        {
            // The old ImagePath may be a user-writable legacy location; never leave it able to auto-start.
            try { serviceControl.Disable(); }
            catch (Exception disableFailure)
            {
                throw new SensorServiceSetupException(
                    "Repair failed and the stopped Sensor Service could not be disabled; uninstall it before retrying.",
                    new AggregateException(exception, disableFailure));
            }
            throw new SensorServiceSetupException(
                "Repair failed; the Sensor Service was stopped and disabled. Run Repair again.", exception);
        }
        serviceControl.Start();
        _payload.TryRemovePrevious();
    }

    public void Uninstall()
    {
        if (serviceControl.Query() is not null)
        {
            serviceControl.Stop();
            serviceControl.Delete();
        }
        try
        {
            _payload.RemoveAll();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new SensorServiceSetupException(
                $"Sensor Service was removed, but {layout.RootDirectory} could not be fully deleted: {exception.Message}", exception);
        }
    }

    private void Deploy(string source)
    {
        security.VerifyParent(layout.ProgramFilesDirectory);
        security.SecureRoot(layout.RootDirectory);
        try
        {
            _payload.Stage(source);
            security.SecureTree(layout.StagingDirectory);
            _payload.Activate();
            security.SecureTree(layout.CurrentDirectory);
        }
        finally
        {
            _payload.TryRemoveStaging();
        }
    }
}
