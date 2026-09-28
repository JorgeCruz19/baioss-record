using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Application.Network;
using Baioss.Record.Infrastructure.Network;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App;

/// <summary>
/// Sección «Panel web y API» de la ventana de Configuración: si la API admite conexiones desde otros equipos, en qué
/// puerto y qué páginas web pueden llamarla. Guarda en <c>data/api-settings.json</c>; se aplica al REINICIAR (el servidor
/// enlaza su dirección al arrancar), y así se dice. También enseña qué escribir en el panel web para conectar.
/// </summary>
public sealed partial class ApiAccessViewModel : ObservableObject
{
    private readonly ApiAccessState _state;
    /// <summary>IP concreta que alguien puso a mano en el JSON (ni 127.0.0.1 ni «toda la red»): se conserva al guardar…</summary>
    private readonly string? _customHost;
    /// <summary>…mientras la casilla siga como estaba: una IP de la red no vale para «solo este equipo», ni al revés.</summary>
    private readonly bool _customIsNetwork;

    public ApiAccessViewModel(ApiAccessState state)
    {
        _state = state;
        var wanted = state.Wanted;
        _allowNetwork = wanted.ListensOnNetwork;
        _port = wanted.Port.ToString();
        _allowedOrigins = wanted.AllowedOrigins;
        _customHost = wanted.Host is ApiAccessSettings.AnyAddress or ApiAccessSettings.Loopback ? null : wanted.Host;
        _customIsNetwork = wanted.ListensOnNetwork;
        // Suscripción DÉBIL al singleton de idioma: esta vista-modelo se crea cada vez que se abre la ventana, y una
        // suscripción normal a un singleton la mantendría viva para siempre (fuga por ventana abierta).
        System.ComponentModel.PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshTexts();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelHint))]
    private bool _allowNetwork;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelHint))]
    private string _port;

    [ObservableProperty] private string _allowedOrigins;

    /// <summary>Resultado de Guardar (o el error de validación).</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>Lo que está pasando AHORA (lo que se aplicó al arrancar), que puede no ser lo guardado.</summary>
    public string AppliedText
    {
        get
        {
            var a = _state.Applied;
            string scope = Loc.T(a.ListensOnNetwork ? "Cfg_Api_ScopeNetwork" : "Cfg_Api_ScopeLocal");
            string text = Loc.F("Cfg_Api_Applied", a.ListenUrl, scope);
            return _state.Warning is null ? text : text + " " + Loc.F("Cfg_Api_Fallback", _state.Warning);
        }
    }

    /// <summary>Qué escribir en «Conexión con el Record» del panel web: las IP de este equipo y el puerto.</summary>
    public string PanelHint
    {
        get
        {
            int port = int.TryParse(Port, out var p) ? p : ApiAccessSettings.DefaultPort;
            if (!AllowNetwork) return Loc.F("Cfg_Api_PanelHintLocal", HostFor(false), port);
            string host = HostFor(true);
            // Escuchando en toda la red vale cualquier IP de este equipo; con una IP concreta, solo esa.
            var ips = host == ApiAccessSettings.AnyAddress ? LocalIPv4().ToList() : new System.Collections.Generic.List<string> { host };
            string where = ips.Count == 0 ? Loc.T("Cfg_Api_NoNetwork") : string.Join("  ·  ", ips);
            return Loc.F("Cfg_Api_PanelHint", where, port);
        }
    }

    private string HostFor(bool network)
        => _customHost is not null && _customIsNetwork == network ? _customHost
            : network ? ApiAccessSettings.AnyAddress : ApiAccessSettings.Loopback;

    // Al abrir a la red, lo normal es querer que el panel web conecte: sin ninguna web permitida el navegador lo
    // bloquearía y el fallo es opaco («no hay respuesta»). Se propone «*»; el operador puede acotarlo.
    partial void OnAllowNetworkChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(AllowedOrigins)) AllowedOrigins = "*";
        StatusText = "";
    }

    [RelayCommand]
    private void Save()
    {
        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText = Loc.T("Cfg_Api_BadPort");
            return;
        }
        var settings = new ApiAccessSettings
        {
            Host = HostFor(AllowNetwork),
            Port = port,
            AllowedOrigins = AllowedOrigins ?? "",
        }.Sanitized();
        try
        {
            ApiAccessSettingsFile.Save(_state.Path, settings);
            AllowedOrigins = settings.AllowedOrigins; // ya normalizados: que se vea lo que se guardó de verdad
            StatusText = Loc.T("Cfg_Api_Saved");
            Serilog.Log.Information("Acceso a la API guardado: {Url}, webs permitidas: {Origins} (se aplica al reiniciar).",
                settings.ListenUrl, settings.Origins.Count == 0 ? "ninguna" : settings.AllowedOrigins);
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("Cfg_Api_SaveError", ex.Message);
            Serilog.Log.Error(ex, "No se pudo guardar el acceso a la API en {Path}.", _state.Path);
        }
    }

    private void RefreshTexts()
    {
        OnPropertyChanged(nameof(AppliedText));
        OnPropertyChanged(nameof(PanelHint));
        StatusText = "";
    }

    /// <summary>IPv4 de las interfaces de red ACTIVAS de este equipo (sin loopback ni las autoasignadas 169.254.x).</summary>
    internal static System.Collections.Generic.IEnumerable<string> LocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Where(ip => !ip.StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
