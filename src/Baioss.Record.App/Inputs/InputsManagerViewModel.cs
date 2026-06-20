using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.App.Inputs;

/// <summary>Una entrada de vídeo elegible (tarjeta DeckLink, cámara DirectShow o archivo de prueba).</summary>
public sealed class InputDeviceOption
{
    public const string NoAudio = "(sin audio)";

    public required string Label { get; init; }
    public required InputType Type { get; init; }

    /// <summary>Nombre del dispositivo (Uri) para DShow/DeckLink, o ruta del archivo para File.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Id determinista del dispositivo (misma fila al reasignar). Null → se genera al aplicar.</summary>
    public Guid? Id { get; init; }

    /// <summary>Modos/formatos SDI del dispositivo (DeckLink), con "Automático" primero. Vacío en el resto.</summary>
    public IReadOnlyList<DeviceFormat> Formats { get; init; } = new[] { DeviceFormat.Auto };

    public override string ToString() => Label;

    /// <summary>Traduce la opción (audio DShow / formato DeckLink elegidos) a una <see cref="InputSource"/>.</summary>
    public InputSource ToInputSource(string? audioDevice, DeviceFormat? format)
    {
        var def = new InputSource
        {
            Id = Id ?? Guid.NewGuid(),
            Name = Label,
            Type = Type,
            Uri = DeviceId,
        };
        if (Type is InputType.File)
        {
            def.Parameters["loop"] = "1";       // el clip demo se reproduce en bucle…
            def.Parameters["realtime"] = "1";   // …a velocidad real, como una fuente en vivo.
        }
        else if (Type is InputType.DirectShow && !string.IsNullOrWhiteSpace(audioDevice) && audioDevice != NoAudio)
        {
            def.Parameters["audio"] = audioDevice!;
        }
        else if (Type is InputType.DecklinkSdi && format is { Code.Length: > 0 })
        {
            def.Parameters["format_code"] = format.Code; // modo SDI elegido (si no, autodetección)
        }
        return def;
    }
}

/// <summary>Fila de asignación de un canal: su entrada de vídeo (+ audio DShow) y el botón Aplicar.</summary>
public sealed partial class ChannelInputRow : ObservableObject
{
    private readonly InputsManagerViewModel _owner;

    public string Key { get; }
    public Guid ChannelId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioEnabled))]
    [NotifyPropertyChangedFor(nameof(FormatEnabled))]
    [NotifyPropertyChangedFor(nameof(Formats))]
    private InputDeviceOption? _selectedDevice;

    [ObservableProperty] private string _selectedAudio = InputDeviceOption.NoAudio;
    [ObservableProperty] private DeviceFormat? _selectedFormat;

    public ChannelInputRow(string key, Guid channelId, InputsManagerViewModel owner)
    {
        Key = key;
        ChannelId = channelId;
        _owner = owner;
    }

    public ObservableCollection<InputDeviceOption> Devices => _owner.VideoDevices;
    public ObservableCollection<string> AudioDevices => _owner.AudioDevices;

    /// <summary>Modos del dispositivo seleccionado (DeckLink); "Automático" siempre disponible.</summary>
    public IReadOnlyList<DeviceFormat> Formats => SelectedDevice?.Formats ?? new[] { DeviceFormat.Auto };

    /// <summary>El audio separado solo aplica a DirectShow (DeckLink lleva el audio embebido en el SDI).</summary>
    public bool AudioEnabled => SelectedDevice?.Type is InputType.DirectShow;

    /// <summary>El selector de modo/formato SDI solo aplica a DeckLink.</summary>
    public bool FormatEnabled => SelectedDevice?.Type is InputType.DecklinkSdi;

    partial void OnSelectedDeviceChanged(InputDeviceOption? value)
        => SelectedFormat = Formats.FirstOrDefault(); // por defecto, "Automático"

    [RelayCommand]
    private Task Apply() => _owner.ApplyRowAsync(this);
}

/// <summary>
/// ViewModel del gestor de entradas. Detecta dispositivos (DeckLink/DirectShow) vía FFmpeg y, por
/// canal, permite elegir una entrada y aplicarla en caliente (reconstruye el canal sin reiniciar).
/// </summary>
public sealed partial class InputsManagerViewModel : ObservableObject
{
    private readonly IDeviceEnumerator _devices;
    private readonly Func<Guid, InputSource, Task> _apply;
    private readonly string? _clipPath;

    public bool CanRebind { get; }
    public ObservableCollection<InputDeviceOption> VideoDevices { get; } = new();
    public ObservableCollection<string> AudioDevices { get; } = new();
    public ObservableCollection<ChannelInputRow> Channels { get; } = new();

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    public InputsManagerViewModel(
        IDeviceEnumerator devices, IReadOnlyList<ChannelViewModel> channels,
        bool canRebind, string? clipPath, Func<Guid, InputSource, Task> apply)
    {
        _devices = devices;
        _apply = apply;
        _clipPath = clipPath;
        CanRebind = canRebind;

        AudioDevices.Add(InputDeviceOption.NoAudio);
        SeedFileOption();
        foreach (var c in channels) Channels.Add(new ChannelInputRow(c.Key, c.ChannelId, this));

        StatusMessage = canRebind
            ? "Pulsa «Detectar» para buscar tarjetas DeckLink y cámaras/capturadoras DirectShow."
            : "Modo simulado (sin FFmpeg): la asignación de entradas no está disponible.";
    }

    private void SeedFileOption()
    {
        if (string.IsNullOrWhiteSpace(_clipPath)) return;
        VideoDevices.Add(new InputDeviceOption
        {
            Label = "Archivo de prueba (clip demo)",
            Type = InputType.File,
            DeviceId = _clipPath,
            Id = StableGuid("input:File:" + _clipPath),
        });
    }

    [RelayCommand]
    private async Task DetectAsync()
    {
        if (!CanRebind) return;
        IsBusy = true;
        StatusMessage = "Detectando dispositivos…";
        try
        {
            VideoDevices.Clear();
            AudioDevices.Clear();
            AudioDevices.Add(InputDeviceOption.NoAudio);
            SeedFileOption();

            int decklinks = 0, decklinkWithModes = 0;
            foreach (var d in await _devices.DiscoverAsync(InputType.DecklinkSdi))
            {
                decklinks++;
                var formats = new List<DeviceFormat> { DeviceFormat.Auto };
                try
                {
                    var modes = await _devices.DiscoverFormatsAsync(InputType.DecklinkSdi, d.Uri ?? d.Name);
                    formats.AddRange(modes);
                    if (modes.Count > 0) decklinkWithModes++;
                    else Serilog.Log.Warning("DeckLink «{Device}»: -list_formats no devolvió modos (¿tarjeta en uso por otra captura, o driver < 12.9?).", d.Name);
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "DeckLink «{Device}»: fallo al detectar formatos.", d.Name); }
                VideoDevices.Add(new InputDeviceOption { Label = $"DeckLink — {d.Name}", Type = InputType.DecklinkSdi, DeviceId = d.Uri, Id = d.Id, Formats = formats });
            }
            foreach (var d in await _devices.DiscoverAsync(InputType.DirectShow))
                VideoDevices.Add(new InputDeviceOption { Label = $"DirectShow — {d.Name}", Type = InputType.DirectShow, DeviceId = d.Uri, Id = d.Id });
            foreach (var a in await _devices.DiscoverAudioDevicesAsync(InputType.DirectShow))
                AudioDevices.Add(a);

            int cards = VideoDevices.Count(d => d.Type is not InputType.File);
            // Aviso si hay tarjetas DeckLink pero ninguna expuso sus modos: el desplegable quedaría solo
            // con «Automático». Causa típica: la tarjeta ya está abierta por la captura del canal.
            string modesHint = decklinks > 0 && decklinkWithModes == 0
                ? " · DeckLink: no se detectaron modos (cierra cualquier captura que use la tarjeta y reintenta; revisa logs)."
                : "";
            StatusMessage = cards == 0
                ? "No se detectaron tarjetas ni cámaras. (¿Drivers DeckLink instalados? ¿cámara conectada?)"
                : $"{cards} entrada(s) de vídeo y {AudioDevices.Count - 1} de audio detectadas." + modesHint;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al detectar: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    internal async Task ApplyRowAsync(ChannelInputRow row)
    {
        if (!CanRebind) { StatusMessage = "No disponible en modo simulado."; return; }
        if (row.SelectedDevice is null) { StatusMessage = $"Canal {row.Key}: elige una entrada de vídeo."; return; }

        IsBusy = true;
        StatusMessage = $"Aplicando «{row.SelectedDevice.Label}» al Canal {row.Key}…";
        try
        {
            var def = row.SelectedDevice.ToInputSource(row.SelectedAudio, row.SelectedFormat);
            await _apply(row.ChannelId, def);
            var mode = row.FormatEnabled && row.SelectedFormat is { Code.Length: > 0 } ? $" · {row.SelectedFormat.Description}" : "";
            StatusMessage = $"Canal {row.Key} → {row.SelectedDevice.Label}{mode}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al aplicar en Canal {row.Key}: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
