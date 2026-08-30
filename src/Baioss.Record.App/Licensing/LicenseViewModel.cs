using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.App.Licensing;

/// <summary>
/// ViewModel de la ventana de Licencia: muestra el estado, deja COPIAR el código de equipo (lo que el cliente
/// envía al proveedor) y pegar la licencia recibida para activarla.
/// </summary>
public sealed partial class LicenseViewModel : ObservableObject
{
    private readonly ILicenseService _license;

    public LicenseViewModel(ILicenseService license)
    {
        _license = license;
        MachineCode = license.MachineCode;
        Apply(license.Current);
        _license.Changed += OnLicenseChanged;
    }

    /// <summary>Código de ESTE equipo. Es lo único que el cliente tiene que enviar para pedir su licencia.</summary>
    public string MachineCode { get; }

    /// <summary>Desengancha el VM del servicio (singleton). La ventana es transitoria y el servicio vive toda la
    /// app: sin esto, cada apertura de la ventana de Licencia dejaba un VM retenido para siempre por el evento.</summary>
    public void Detach() => _license.Changed -= OnLicenseChanged;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isLicensed;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private string _licenseKeyInput = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;

    private void OnLicenseChanged(object? sender, LicenseInfo info)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply(info));

    private void Apply(LicenseInfo info)
    {
        StatusText = info.Summary;
        IsLicensed = info.State is LicenseState.Licensed;
        NeedsAttention = info.NeedsAttention;
        DetailText = info.State switch
        {
            LicenseState.Licensed => info.LicensedChannels is int c
                ? $"Este equipo tiene una licencia permanente de {c} canal{(c == 1 ? "" : "es")}. No caduca. " +
                  "Para ampliar canales, pide una licencia nueva a tu proveedor."
                : "Este equipo tiene una licencia permanente. No caduca.",
            LicenseState.Trial => "Puedes usar todas las funciones durante el periodo de prueba. "
                                + "Cuando termine podrás seguir viendo las entradas, pero no iniciar grabaciones nuevas.",
            LicenseState.Expired => "El periodo de prueba ha terminado. Las grabaciones en curso NO se interrumpen, "
                                  + "pero no se pueden iniciar nuevas hasta activar la licencia.",
            _ => "No se pudo comprobar el estado de la licencia. La grabación sigue disponible; revisa el registro de la aplicación.",
        };
    }

    [RelayCommand]
    private void CopyMachineCode()
    {
        try
        {
            Clipboard.SetText(MachineCode);
            Message = "Código de equipo copiado. Envíaselo a tu proveedor para recibir la licencia.";
            MessageIsError = false;
        }
        catch
        {
            // El portapapeles puede estar tomado por otra app: no es motivo para romper nada.
            Message = "No se pudo copiar automáticamente; selecciona el código y cópialo a mano.";
            MessageIsError = true;
        }
    }

    [RelayCommand]
    private void Activate()
    {
        var result = _license.Activate(LicenseKeyInput);
        Message = result.Message;
        MessageIsError = !result.Success;
        if (!result.Success) return;
        LicenseKeyInput = "";

        // Los canales comprados se componen AL ARRANCAR (mín(instalador, licenciados)): se avisa y se
        // reinicia la app con el apagado ORDENADO de siempre — si hay grabaciones en curso, se finalizan
        // correctamente antes de cerrar; jamás se mata el proceso a lo bruto.
        var info = _license.Current;
        string channels = info.LicensedChannels is int c ? $" de {c} canal{(c == 1 ? "" : "es")}" : "";
        MessageBox.Show(
            $"Licencia activada{channels}. Gracias.\n\n" +
            "La aplicación se reiniciará ahora para aplicar los canales de tu licencia. " +
            "Si hay alguna grabación en curso, se finalizará correctamente antes de cerrar.",
            "Licencia activada", MessageBoxButton.OK, MessageBoxImage.Information);
        (System.Windows.Application.Current as App)?.RestartToApplyLicenseAsync();
    }
}
