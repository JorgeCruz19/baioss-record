using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Persistence;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App.Inputs;

/// <summary>Una fuente de red guardada, para la lista del diálogo: «Enlace estudio · SRT · escucha en 0.0.0.0:9000».</summary>
public sealed record NetworkSourceItem(Guid Id, string Name, NetworkInput Input)
{
    /// <summary>«SRT · escucha en 0.0.0.0:9000», en el idioma del operador (para la lista).</summary>
    public string Description => Input.Describe();
    public override string ToString() => $"{Name} · {Description}";
}

/// <summary>Opción de un desplegable con su etiqueta en el idioma del operador.</summary>
public sealed record ChoiceOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Diálogo «Fuentes de red»: alta, edición y baja de entradas SRT/RTMP, persistidas como <see cref="InputSource"/> y
/// asignables después a un canal desde el gestor de entradas. El formulario es texto plano que valida
/// <see cref="NetworkInput"/>; nada se guarda hasta pulsar Guardar. <see cref="Changed"/> le dice al gestor de
/// entradas si tiene que refrescar sus desplegables.
/// </summary>
public sealed partial class NetworkSourcesViewModel : ObservableObject
{
    private readonly IInputSourceRepository _repo;
    private readonly Func<Guid, bool> _isInUse;
    private readonly string _thisHost;

    public NetworkSourcesViewModel(IInputSourceRepository repo, Func<Guid, bool> isInUse, string thisHost)
    {
        _repo = repo;
        _isInUse = isInUse;
        _thisHost = thisHost;
        Protocols = new[]
        {
            new ChoiceOption<NetworkProtocol>(NetworkProtocol.Srt, "SRT"),
            new ChoiceOption<NetworkProtocol>(NetworkProtocol.Rtmp, "RTMP"),
        };
        NewSource();
    }

    public ObservableCollection<NetworkSourceItem> Sources { get; } = new();
    public IReadOnlyList<ChoiceOption<NetworkProtocol>> Protocols { get; }

    /// <summary>Los roles se nombran según el protocolo («el emisor llama» / «el emisor publica»).</summary>
    [ObservableProperty] private IReadOnlyList<ChoiceOption<NetworkRole>> _roles = Array.Empty<ChoiceOption<NetworkRole>>();

    [ObservableProperty] private NetworkSourceItem? _selected;

    /// <summary>Id de la fuente que se edita; null = alta nueva.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private Guid? _editingId;

    [ObservableProperty] private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSrt))]
    [NotifyPropertyChangedFor(nameof(IsRtmp))]
    [NotifyPropertyChangedFor(nameof(ShowStreamId))]
    [NotifyPropertyChangedFor(nameof(ShowSecure))]
    private ChoiceOption<NetworkProtocol>? _protocol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListen))]
    [NotifyPropertyChangedFor(nameof(ShowStreamId))]
    [NotifyPropertyChangedFor(nameof(ShowSecure))]
    [NotifyPropertyChangedFor(nameof(HostHint))]
    private ChoiceOption<NetworkRole>? _role;

    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _passphrase = "";
    [ObservableProperty] private string _latencyMs = NetworkInput.DefaultLatencyMs.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string _streamId = "";
    [ObservableProperty] private string _audioDelayMs = "0";
    [ObservableProperty] private string _previewBufferMs = "0";
    [ObservableProperty] private bool _secure;
    [ObservableProperty] private string _pasteUrl = "";

    /// <summary>«Se abrirá como SRT · escucha en 0.0.0.0:9000 · srt://0.0.0.0:9000», o el error del formulario.</summary>
    [ObservableProperty] private string _preview = "";
    /// <summary>Lo que hay que poner en el emisor (con «escuchar»), o que no hace falta nada (con «llamar»).</summary>
    [ObservableProperty] private string _encoderHint = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>True si se guardó o eliminó algo: el gestor de entradas refresca sus desplegables al cerrar.</summary>
    public bool Changed { get; private set; }

    public bool IsSrt => Protocol?.Value == NetworkProtocol.Srt;
    public bool IsRtmp => Protocol?.Value == NetworkProtocol.Rtmp;
    public bool IsListen => Role?.Value == NetworkRole.Listen;
    public bool ShowStreamId => IsSrt && !IsListen;
    public bool ShowSecure => IsRtmp && !IsListen;
    public bool CanDelete => EditingId is not null;
    public string HostHint => Loc.T(IsListen ? "Net_HostHint_Listen" : "Net_HostHint_Connect");

    public async Task LoadAsync()
    {
        var all = await _repo.ListAsync();
        Sources.Clear();
        foreach (var s in all.Where(s => NetworkInput.IsNetworkType(s.Type)).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            if (NetworkInput.FromInputSource(s) is { } input) Sources.Add(new NetworkSourceItem(s.Id, s.Name, input));
    }

    partial void OnSelectedChanged(NetworkSourceItem? value)
    {
        if (value is null) return;
        var n = value.Input;
        EditingId = value.Id;
        Name = value.Name;
        Protocol = Protocols.First(p => p.Value == n.Protocol);
        Role = Roles.First(r => r.Value == n.Role);
        Host = n.Host == NetworkInput.AnyAddress && n.Role == NetworkRole.Listen ? "" : n.Host;
        Port = n.Port.ToString(CultureInfo.InvariantCulture);
        Path = n.Path;
        Passphrase = n.Passphrase ?? "";
        LatencyMs = n.LatencyMs.ToString(CultureInfo.InvariantCulture);
        StreamId = n.StreamId ?? "";
        Secure = n.Secure;
        AudioDelayMs = n.AudioDelayMs.ToString(CultureInfo.InvariantCulture);
        PreviewBufferMs = n.PreviewBufferMs.ToString(CultureInfo.InvariantCulture);
        Status = "";
    }

    partial void OnProtocolChanged(ChoiceOption<NetworkProtocol>? value) { UpdateRoles(); RefreshPreview(); }
    partial void OnRoleChanged(ChoiceOption<NetworkRole>? value) => RefreshPreview();
    partial void OnHostChanged(string value) => RefreshPreview();
    partial void OnPortChanged(string value) => RefreshPreview();
    partial void OnPathChanged(string value) => RefreshPreview();
    partial void OnPassphraseChanged(string value) => RefreshPreview();
    partial void OnLatencyMsChanged(string value) => RefreshPreview();
    partial void OnStreamIdChanged(string value) => RefreshPreview();
    partial void OnAudioDelayMsChanged(string value) => RefreshPreview();
    partial void OnPreviewBufferMsChanged(string value) => RefreshPreview();
    partial void OnSecureChanged(bool value) => RefreshPreview();

    private void UpdateRoles()
    {
        bool srt = IsSrt;
        var current = Role?.Value ?? NetworkRole.Listen;
        Roles = new[]
        {
            new ChoiceOption<NetworkRole>(NetworkRole.Listen, Loc.T(srt ? "Net_Role_SrtListen" : "Net_Role_RtmpListen")),
            new ChoiceOption<NetworkRole>(NetworkRole.Connect, Loc.T(srt ? "Net_Role_SrtConnect" : "Net_Role_RtmpConnect")),
        };
        Role = Roles.First(r => r.Value == current);
    }

    /// <summary>El formulario como definición, o null con el motivo (puerto/latencia no numéricos cuentan como fuera de rango).</summary>
    private NetworkInput? Build(out NetworkInputError error)
    {
        bool rtmp = IsRtmp;
        int port = int.TryParse(Port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p
            : rtmp && Port.Trim().Length == 0 ? NetworkInput.DefaultRtmpPort : 0;
        int latency = int.TryParse(LatencyMs.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : -1;
        // Vacío = 0; no numérico = fuera de rango (para que el mensaje lo diga).
        int audioDelay = AudioDelayMs.Trim().Length == 0 ? 0
            : int.TryParse(AudioDelayMs.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : int.MaxValue;
        int previewBuffer = PreviewBufferMs.Trim().Length == 0 ? 0
            : int.TryParse(PreviewBufferMs.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : int.MaxValue;
        var input = new NetworkInput
        {
            Protocol = Protocol?.Value ?? NetworkProtocol.Srt,
            Role = Role?.Value ?? NetworkRole.Listen,
            Host = Host, Port = port, Path = Path, Passphrase = Passphrase, LatencyMs = latency, StreamId = StreamId, Secure = Secure,
            AudioDelayMs = audioDelay, PreviewBufferMs = previewBuffer,
        }.Normalized();
        error = input.Validate();
        return error == NetworkInputError.None ? input : null;
    }

    private void RefreshPreview()
    {
        // Formulario en blanco (recién abierto o tras «Nueva»): una pista, no un error por un puerto que aún no se ha escrito.
        if (Port.Trim().Length == 0 && Host.Trim().Length == 0 && Path.Trim().Length == 0)
        {
            Preview = Loc.T("Net_Preview_Empty");
            EncoderHint = "";
            return;
        }
        if (Build(out var error) is { } input)
        {
            // Al llamar por RTMP la descripción ya es la URL: no repetirla («RTMP → rtmp://x · rtmp://x»).
            string described = input.Describe();
            Preview = described.Contains(input.Url, StringComparison.Ordinal) ? Loc.F("Net_PreviewShort", described) : Loc.F("Net_Preview", described, input.Url);
            EncoderHint = input.EncoderHint(_thisHost) ?? Loc.T("Net_Hint_Connect");
        }
        else
        {
            Preview = Loc.T(ErrorKey(error));
            EncoderHint = "";
        }
    }

    [RelayCommand]
    private void NewSource()
    {
        Selected = null;
        EditingId = null;
        Name = "";
        Protocol = Protocols[0];
        Role = Roles.FirstOrDefault(r => r.Value == NetworkRole.Listen) ?? Roles.FirstOrDefault();
        Host = ""; Port = ""; Path = ""; Passphrase = "";
        LatencyMs = NetworkInput.DefaultLatencyMs.ToString(CultureInfo.InvariantCulture);
        StreamId = ""; Secure = false; PasteUrl = ""; AudioDelayMs = "0"; PreviewBufferMs = "0";
        Status = "";
        RefreshPreview();
    }

    /// <summary>Rellena el formulario con una URL pegada de otro programa (OBS, vMix, un servidor…).</summary>
    [RelayCommand]
    private void FillFromUrl()
    {
        if (!NetworkInput.TryParseUrl(PasteUrl, out var input, out var error) || input is null)
        {
            Status = Loc.T(ErrorKey(error));
            return;
        }
        Protocol = Protocols.First(p => p.Value == input.Protocol);
        Role = Roles.First(r => r.Value == input.Role);
        Host = input.Host == NetworkInput.AnyAddress && input.Role == NetworkRole.Listen ? "" : input.Host;
        Port = input.Port.ToString(CultureInfo.InvariantCulture);
        Path = input.Path;
        Passphrase = input.Passphrase ?? "";
        LatencyMs = input.LatencyMs.ToString(CultureInfo.InvariantCulture);
        StreamId = input.StreamId ?? "";
        Secure = input.Secure;
        Status = Loc.T("Net_Msg_Filled");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        string name = Name.Trim();
        if (name.Length == 0) { Status = Loc.T("Net_Msg_NameRequired"); return; }
        if (Build(out var error) is not { } input) { Status = Loc.T(ErrorKey(error)); return; }
        // Dos fuentes escuchando en el mismo puerto no pueden convivir: la segunda no podría abrir.
        if (input.Role == NetworkRole.Listen && Sources.Any(s => s.Id != EditingId && s.Input.Role == NetworkRole.Listen
                                                                 && s.Input.Protocol == input.Protocol && s.Input.Port == input.Port))
        {
            Status = Loc.T("Net_Err_PortInUse");
            return;
        }

        IsBusy = true;
        try
        {
            var id = EditingId ?? Guid.NewGuid();
            var def = input.ToInputSource(id, name);
            if (EditingId is null) await _repo.AddAsync(def);
            else await _repo.UpdateAsync(def);
            Changed = true;
            await LoadAsync();
            Selected = Sources.FirstOrDefault(s => s.Id == id);
            Status = Loc.F("Net_Msg_Saved", name);
        }
        catch (Exception ex) { Status = Loc.F("Net_Msg_Error", ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditingId is not { } id) return;
        if (_isInUse(id)) { Status = Loc.T("Net_Msg_InUse"); return; }
        IsBusy = true;
        try
        {
            string name = Name;
            await _repo.RemoveAsync(id);
            Changed = true;
            await LoadAsync();
            NewSource();
            Status = Loc.F("Net_Msg_Deleted", name);
        }
        catch (Exception ex) { Status = Loc.F("Net_Msg_Error", ex.Message); }
        finally { IsBusy = false; }
    }

    private static string ErrorKey(NetworkInputError error) => error switch
    {
        NetworkInputError.MissingHost => "Net_Err_MissingHost",
        NetworkInputError.InvalidHost => "Net_Err_InvalidHost",
        NetworkInputError.InvalidPort => "Net_Err_InvalidPort",
        NetworkInputError.InvalidPath => "Net_Err_InvalidPath",
        NetworkInputError.PassphraseLength => "Net_Err_Passphrase",
        NetworkInputError.LatencyRange => "Net_Err_Latency",
        NetworkInputError.StreamIdTooLong => "Net_Err_StreamId",
        NetworkInputError.AudioDelayRange => "Net_Err_AudioDelay",
        NetworkInputError.PreviewBufferRange => "Net_Err_PreviewBuffer",
        NetworkInputError.UnsupportedScheme => "Net_Err_Scheme",
        _ => "Net_Err_InvalidUrl",
    };
}
