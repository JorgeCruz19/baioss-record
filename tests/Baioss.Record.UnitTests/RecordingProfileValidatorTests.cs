using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Localization;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

[Collection("Localizer")] // los mensajes salen en el idioma vigente (estado global)
public class RecordingProfileValidatorTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public RecordingProfileValidatorTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static RecordingProfile Valid() => new()
    {
        Name = "ok", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50, RateControl = RateControlMode.ConstantBitrate,
        AudioCodec = AudioCodec.Aac, AudioBitrate = Bitrate.FromKbps(256), AudioSampleRate = 48_000,
        Container = ContainerFormat.Mp4,
    };

    [Fact]
    public void Valid_ProfileHasNoErrors() => Assert.Empty(RecordingProfileValidator.Validate(Valid()));

    [Fact]
    public void Zero_VideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Odd_Resolution_IsRejected()
    {
        var p = Valid(); p.TargetResolution = new Resolution(1281, 720);
        Assert.Contains(RecordingProfileValidator.Validate(p), e => e.Contains("PARES"));
    }

    [Fact]
    public void Messages_FollowTheApplicationLanguage()
    {
        // Lo ve el operador (editor de presets, badge del canal, aviso al pulsar Grabar): no puede salir en español con la
        // interfaz en inglés.
        var p = Valid(); p.TargetResolution = new Resolution(1281, 720);
        Localizer.Language = AppLanguage.English;
        Assert.Contains(RecordingProfileValidator.Validate(p), e => e.Contains("EVEN"));
    }

    [Fact]
    public void Zero_Gop_IsRejected()
    {
        var p = Valid(); p.GopSize = 0;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Crf_OutOfRange_IsRejected()
    {
        var p = Valid(); p.RateControl = RateControlMode.ConstantQuality; p.Quality = 99;
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void MaxBitrate_BelowVideoBitrate_IsRejected()
    {
        var p = Valid(); p.VideoBitrate = Bitrate.FromMbps(8); p.MaxBitrate = Bitrate.FromMbps(4);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Zero_AudioBitrate_IsRejected_ForCompressedAudio()
    {
        var p = Valid(); p.AudioCodec = AudioCodec.Aac; p.AudioBitrate = new Bitrate(0);
        Assert.NotEmpty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void Intra_ZeroBitrate_IsAllowed()
    {
        // ProRes va por -profile, no por -b:v: un bitrate 0 NO es un error en un códec intra.
        var p = Valid();
        p.VideoCodec = VideoCodec.ProRes; p.VideoBitrate = new Bitrate(0); p.Container = ContainerFormat.Mov;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    [Fact]
    public void ConstantQuality_ZeroBitrate_IsAllowed()
    {
        // CRF/CQ no usa -b:v: bitrate 0 es legítimo.
        var p = Valid();
        p.RateControl = RateControlMode.ConstantQuality; p.Quality = 23; p.VideoBitrate = new Bitrate(0);
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    // --- Contenedor × códec de vídeo ---

    private static readonly ContainerFormat[] VideoContainers =
    {
        ContainerFormat.Mp4, ContainerFormat.Mov, ContainerFormat.Mxf, ContainerFormat.Mkv,
        ContainerFormat.Ts, ContainerFormat.Avi, ContainerFormat.ProgramStream,
    };

    private enum Outcome { Records, Refused, Unreadable }

    /// <summary>
    /// Lo que hizo FFmpeg (8.0 y el incluido, iguales) el 2026-10-18 al grabar 2 s con cada códec en cada contenedor y releer
    /// el archivo: Refused = el muxer lo rechaza y la grabación no arranca; Unreadable = lo escribe sin error y se relee como
    /// rawvideo (HEVC en AVI) o como datos privados/desconocidos (AV1, ProRes, DNxHD/HR en TS y PS). El resto se relee entero.
    /// </summary>
    private static Outcome Measured(VideoCodec codec, ContainerFormat container) => (codec, container) switch
    {
        (VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc, ContainerFormat.Avi) => Outcome.Unreadable,
        (VideoCodec.H265x265 or VideoCodec.HevcQsv or VideoCodec.HevcNvenc, ContainerFormat.Mxf) => Outcome.Refused,
        (VideoCodec.Av1Nvenc, ContainerFormat.Mxf or ContainerFormat.Mov) => Outcome.Refused,
        (VideoCodec.Av1Nvenc, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Outcome.Unreadable,
        (VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr, ContainerFormat.Mp4) => Outcome.Refused,
        (VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr, ContainerFormat.Ts or ContainerFormat.ProgramStream) => Outcome.Unreadable,
        _ => Outcome.Records,
    };

    public static TheoryData<VideoCodec, ContainerFormat> EveryVideoCodecInEveryVideoContainer()
    {
        var data = new TheoryData<VideoCodec, ContainerFormat>();
        foreach (var codec in Enum.GetValues<VideoCodec>())
            foreach (var container in VideoContainers)
                data.Add(codec, container);
        return data;
    }

    /// <summary>Ese vídeo en ese contenedor, con un audio que el contenedor lleva: así lo único que puede fallar es el vídeo.</summary>
    private static RecordingProfile With(VideoCodec codec, ContainerFormat container)
    {
        var p = Valid(); p.VideoCodec = codec; p.Container = container; p.AudioCodec = AudioThatFits(container);
        return p;
    }

    private static AudioCodec AudioThatFits(ContainerFormat container) => container switch
    {
        ContainerFormat.Mxf or ContainerFormat.Wav => AudioCodec.Pcm,
        ContainerFormat.ProgramStream => AudioCodec.Mp2,
        ContainerFormat.Mp3Audio => AudioCodec.Mp3,
        _ => AudioCodec.Aac,
    };

    [Theory]
    [MemberData(nameof(EveryVideoCodecInEveryVideoContainer))]
    public void ContainerAndCodec_AreRejected_ExactlyWhereFfmpegCannotRecordThem(VideoCodec codec, ContainerFormat container)
    {
        var errors = RecordingProfileValidator.Validate(With(codec, container));

        switch (Measured(codec, container))
        {
            case Outcome.Records:
                Assert.Empty(errors);
                break;
            case Outcome.Refused:
                var refused = Assert.Single(errors);
                Assert.Contains($"no se puede grabar en {container}", refused);
                Assert.Contains("la grabación no arrancaría", refused);
                Assert.Contains(codec.ToString(), refused); // el nombre que el operador eligió en el editor
                break;
            case Outcome.Unreadable:
                var unreadable = Assert.Single(errors);
                Assert.Contains($"no se puede grabar en {container}", unreadable);
                Assert.Contains("sale ilegible", unreadable);
                Assert.Contains(codec.ToString(), unreadable);
                break;
        }
    }

    [Fact]
    public void HevcInAvi_SaysWhyAndWhatToChoose_InSpanish()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.HevcQsv, ContainerFormat.Avi)));
        Assert.Equal("HevcQsv (HEVC) no se puede grabar en Avi: FFmpeg escribe el archivo sin dar error, pero sale ilegible. " +
                     "Para HEVC, elige Mp4, Mov, Mkv o Ts.", error);
    }

    [Fact]
    public void HevcInMxf_SaysWhyAndWhatToChoose_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.H265x265, ContainerFormat.Mxf)));
        Assert.Equal("H265x265 (HEVC) can't be recorded in Mxf: FFmpeg doesn't support that combination, so the recording " +
                     "wouldn't start. For HEVC, choose Mp4, Mov, Mkv or Ts.", error);
    }

    [Fact]
    public void ACodecNamedLikeItsFamily_IsNotRepeated()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.ProRes, ContainerFormat.Mp4)));
        Assert.StartsWith("ProRes no se puede grabar en Mp4:", error);
    }

    [Fact]
    public void DnxInMp4_AlsoSuggestsAvi()
    {
        // En AVI el DNxHR sale con «AVdh» y el DNxHD clásico con el «AVdn» que le pone FfmpegArgumentBuilder: los dos se releen.
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.DnxHd, ContainerFormat.Mp4)));
        Assert.Equal("DnxHd (DNxHD/DNxHR) no se puede grabar en Mp4: FFmpeg no admite esa combinación y la grabación no " +
                     "arrancaría. Para DNxHD/DNxHR, elige Mxf, Mov, Mkv o Avi.", error);
    }

    [Theory]
    [MemberData(nameof(EveryVideoCodecInEveryVideoContainer))]
    public void TheContainersTheMessageSuggests_DoRecordThatCodec(VideoCodec codec, ContainerFormat container)
    {
        Localizer.Language = AppLanguage.English;
        var errors = RecordingProfileValidator.Validate(With(codec, container));
        if (errors.Count == 0) return; // esta combinación graba: no hay nada que proponer

        // «… For HEVC, choose Mp4, Mov, Mkv or Ts.»: cada propuesta tiene que pasar la validación con el mismo códec.
        string message = Assert.Single(errors);
        string list = message[(message.LastIndexOf("choose ", StringComparison.Ordinal) + "choose ".Length)..].TrimEnd('.');
        var suggested = list.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ").Select(s => Enum.Parse<ContainerFormat>(s)).ToList();
        Assert.True(suggested.Count >= 2, message);
        foreach (var alternative in suggested)
            Assert.Empty(RecordingProfileValidator.Validate(With(codec, alternative)));
    }

    [Theory]
    [InlineData(ContainerFormat.Wav)]
    [InlineData(ContainerFormat.Mp3Audio)]
    public void VideoInAnAudioOnlyContainer_IsRejected(ContainerFormat container)
    {
        // El muxer WAV rechaza la pista de vídeo («wav muxer does not support any stream of type video») y el MP3 falla al
        // escribirla: sin «Solo audio», un perfil con WAV/MP3 no graba.
        var error = Assert.Single(RecordingProfileValidator.Validate(With(VideoCodec.H264x264, container)));
        Assert.Equal($"El contenedor {container} solo lleva audio: marca «Solo audio» o elige un contenedor de vídeo.", error);
    }

    [Theory]
    [InlineData(ContainerFormat.Wav, AudioCodec.Pcm)]
    [InlineData(ContainerFormat.Mp3Audio, AudioCodec.Mp3)]
    [InlineData(ContainerFormat.Avi, AudioCodec.Pcm)] // el códec de vídeo que quedara en un perfil de solo audio no cuenta
    public void AudioOnlyProfiles_IgnoreTheVideoCodec(ContainerFormat container, AudioCodec audio)
    {
        var p = With(VideoCodec.HevcQsv, container);
        p.AudioOnly = true; p.AudioCodec = audio;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    // --- Contenedor × códec de audio ---

    private enum AudioOutcome { Records, Refused, Unreadable }

    /// <summary>
    /// Lo que hizo el motor real (FfmpegChannelEngine, el argv del producto) el 2026-10-18 con FFmpeg 8.0 y con el incluido al
    /// grabar 4 s de un tono con cada códec de audio en cada contenedor, con vídeo y en solo audio, y releer el archivo:
    /// Refused = el muxer no escribe la cabecera y no queda audio; Unreadable = se escribe sin error y no se decodifica (AAC en
    /// WAV). El resto se relee entero; PCM en MP4, MOV y TS, como AAC. FDK-AAC en AVI y WAV lo graba 8.0, pero el incluido lo
    /// rechaza. Además, MXF en solo audio no graba con ningún códec: necesita vídeo (<see cref="AudioOnlyMxf_NeedsAVideoTrack"/>).
    /// </summary>
    private static AudioOutcome MeasuredAudio(AudioCodec audio, ContainerFormat container) => (audio, container) switch
    {
        (AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus or AudioCodec.Mp3 or AudioCodec.Mp2, ContainerFormat.Mxf) => AudioOutcome.Refused,
        (AudioCodec.Pcm or AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus, ContainerFormat.ProgramStream) => AudioOutcome.Refused,
        (AudioCodec.Pcm or AudioCodec.Aac or AudioCodec.FdkAac or AudioCodec.Opus or AudioCodec.Mp2, ContainerFormat.Mp3Audio) => AudioOutcome.Refused,
        (AudioCodec.Opus, ContainerFormat.Avi or ContainerFormat.Mov or ContainerFormat.Wav) => AudioOutcome.Refused,
        (AudioCodec.FdkAac, ContainerFormat.Avi or ContainerFormat.Wav) => AudioOutcome.Refused,
        (AudioCodec.Aac, ContainerFormat.Wav) => AudioOutcome.Unreadable,
        _ => AudioOutcome.Records,
    };

    /// <summary>Cada códec de audio en cada contenedor: con vídeo en los siete de vídeo y en solo audio en los nueve.</summary>
    public static TheoryData<AudioCodec, ContainerFormat, bool> EveryAudioCodecInEveryContainer()
    {
        var data = new TheoryData<AudioCodec, ContainerFormat, bool>();
        foreach (var audio in Enum.GetValues<AudioCodec>())
        {
            foreach (var container in VideoContainers) data.Add(audio, container, false);
            foreach (var container in Enum.GetValues<ContainerFormat>()) data.Add(audio, container, true);
        }
        return data;
    }

    /// <summary>Ese audio en ese contenedor, con vídeo H.264 (lo llevan los siete contenedores de vídeo) o en solo audio.</summary>
    private static RecordingProfile WithAudio(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        var p = Valid(); p.AudioCodec = audio; p.Container = container; p.AudioOnly = audioOnly;
        return p;
    }

    private const string MxfNeedsVideo = "El contenedor Mxf necesita una pista de vídeo: desmarca «Solo audio» o elige otro contenedor.";

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void AudioAndContainer_AreRejected_ExactlyWhereTheEngineCannotRecordThem(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        var errors = RecordingProfileValidator.Validate(WithAudio(audio, container, audioOnly)).ToList();
        if (audioOnly && container is ContainerFormat.Mxf)
        {
            // Sin vídeo, MXF no graba con ningún audio: lo dice primero, y después lo del códec si también falla.
            Assert.Equal(MxfNeedsVideo, errors.FirstOrDefault());
            errors.RemoveAt(0);
        }

        switch (MeasuredAudio(audio, container))
        {
            case AudioOutcome.Records:
                Assert.Empty(errors);
                break;
            case AudioOutcome.Refused:
                var refused = Assert.Single(errors);
                Assert.StartsWith($"El audio {audio} no se puede grabar en {container}: ", refused); // el nombre del editor
                Assert.Contains("la grabación no arrancaría", refused);
                break;
            case AudioOutcome.Unreadable:
                var unreadable = Assert.Single(errors);
                Assert.StartsWith($"El audio {audio} no se puede grabar en {container}: ", unreadable);
                Assert.Contains("el audio sale ilegible", unreadable);
                break;
        }
    }

    [Fact]
    public void AacInMxf_SaysWhyAndWhichAudioToChoose_InSpanish()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Mxf, audioOnly: false)));
        Assert.Equal("El audio Aac no se puede grabar en Mxf: FFmpeg no admite esa combinación y la grabación no arrancaría. " +
                     "En Mxf, elige el audio Pcm.", error);
    }

    [Fact]
    public void OpusInProgramStream_SaysWhyAndWhichAudioToChoose_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Opus, ContainerFormat.ProgramStream, audioOnly: false)));
        Assert.Equal("Opus audio can't be recorded in ProgramStream: FFmpeg doesn't support that combination, so the recording " +
                     "wouldn't start. In ProgramStream, choose Mp2 or Mp3 audio.", error);
    }

    [Fact]
    public void AacInWav_SaysTheAudioComesOutUnreadable()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Wav, audioOnly: true)));
        Assert.Equal("El audio Aac no se puede grabar en Wav: FFmpeg escribe el archivo sin dar error, pero el audio sale ilegible. " +
                     "En Wav, elige el audio Pcm.", error);
    }

    [Fact]
    public void AudioOnlyMxf_NeedsAVideoTrack()
    {
        // El muxer MXF exige que la primera pista sea de vídeo: en solo audio no graba ni con PCM. Con un audio que MXF tampoco
        // lleva, los dos motivos.
        Assert.Equal(MxfNeedsVideo, Assert.Single(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.Mxf, audioOnly: true))));
        var both = RecordingProfileValidator.Validate(WithAudio(AudioCodec.Aac, ContainerFormat.Mxf, audioOnly: true));
        Assert.Equal(2, both.Count);
        Assert.Equal(MxfNeedsVideo, both[0]);
        Assert.StartsWith("El audio Aac no se puede grabar en Mxf: ", both[1]);
        // Con vídeo, el PCM sí va en MXF.
        Assert.Empty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.Mxf, audioOnly: false)));
    }

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void TheAudioTheMessageSuggests_DoesRecordInThatContainer(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Localizer.Language = AppLanguage.English;
        string? message = RecordingProfileValidator.Validate(WithAudio(audio, container, audioOnly))
            .SingleOrDefault(e => e.StartsWith($"{audio} audio can't be recorded", StringComparison.Ordinal));
        if (message is null) return; // este audio cabe en el contenedor: no hay nada que proponer

        // «… In ProgramStream, choose Mp2 or Mp3 audio.»: con cada propuesta el audio deja de ser un problema; solo puede quedar
        // que MXF necesita vídeo, que es otro motivo con su propio mensaje.
        int from = message.LastIndexOf("choose ", StringComparison.Ordinal) + "choose ".Length;
        string list = message[from..message.LastIndexOf(" audio.", StringComparison.Ordinal)];
        var suggested = list.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ").Select(s => Enum.Parse<AudioCodec>(s)).ToList();
        Assert.NotEmpty(suggested);
        foreach (var alternative in suggested)
            Assert.All(RecordingProfileValidator.Validate(WithAudio(alternative, container, audioOnly)),
                e => Assert.StartsWith("The Mxf container needs a video track", e));
    }

    [Theory]
    [InlineData(ContainerFormat.Mp4)]
    [InlineData(ContainerFormat.Mov)]
    [InlineData(ContainerFormat.Ts)]
    public void PcmInMp4MovOrTs_Passes_BecauseTheEngineRecordsItAsAac(ContainerFormat container)
    {
        // PCM s24le no cabe en estos tres (el TS lo guardaría como datos privados), pero el motor lo graba como AAC. Si dejara de
        // promoverlo, esta prueba avisa: el validador tendría que rechazarlo.
        Assert.Equal("aac", FfmpegCodecMap.EffectiveAudioEncoder(AudioCodec.Pcm, container));
        Assert.Empty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, container, audioOnly: false)));
    }

    [Fact]
    public void PcmInProgramStream_IsRejected_BecauseTheEngineKeepsIt24Bit()
    {
        // El PS de FFmpeg solo lleva PCM de 16 bits (pcm_dvd, pcm_s16be) y el motor no promueve ahí el PCM. Si empezara a hacerlo,
        // esta prueba avisa: el validador estaría rechazando algo que ya graba.
        Assert.Equal("pcm_s24le", FfmpegCodecMap.EffectiveAudioEncoder(AudioCodec.Pcm, ContainerFormat.ProgramStream));
        Assert.NotEmpty(RecordingProfileValidator.Validate(WithAudio(AudioCodec.Pcm, ContainerFormat.ProgramStream, audioOnly: false)));
    }

    // --- Frecuencia de muestreo del audio ---

    /// <summary>Las frecuencias de la medida: las estándar de 7350 a 384000 Hz y la de 48 kHz con «pull-down» (47952).</summary>
    private static readonly int[] MeasuredRates =
        { 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 47952, 48000, 64000, 88200, 96000, 176400, 192000, 384000 };

    /// <summary>El codificador que pone el motor: el PCM de MP4, MOV y TS es AAC (<see cref="PcmInMp4MovOrTs_Passes_BecauseTheEngineRecordsItAsAac"/>).</summary>
    private static AudioCodec Encoded(AudioCodec audio, ContainerFormat container) =>
        audio is AudioCodec.Pcm && container is ContainerFormat.Mp4 or ContainerFormat.Mov or ContainerFormat.Ts ? AudioCodec.Aac : audio;

    /// <summary>Si el codificador abre esa frecuencia: las que declara FFmpeg («Supported sample rates»); con otra, «Specified sample
    /// rate N is not supported by the X encoder». El PCM, cualquiera.</summary>
    private static bool EncoderOpens(AudioCodec encoded, int rate) => encoded switch
    {
        AudioCodec.Pcm => true,
        AudioCodec.Aac => rate is 7350 or 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000,
        AudioCodec.FdkAac => rate is 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 64000 or 88200 or 96000,
        AudioCodec.Opus => rate is 8000 or 12000 or 16000 or 24000 or 48000,
        AudioCodec.Mp3 => rate is 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000,
        AudioCodec.Mp2 => rate is 16000 or 22050 or 24000 or 32000 or 44100 or 48000,
        _ => throw new ArgumentOutOfRangeException(nameof(encoded)),
    };

    /// <summary>
    /// Lo que hizo el motor real (FfmpegChannelEngine, el argv del producto, que pasa siempre -ar) el 2026-10-18 con FFmpeg 8.0 y
    /// con el incluido, iguales, al grabar 4 s de un tono con cada audio en cada contenedor que lo lleva, con vídeo y en solo audio,
    /// a cada una de <see cref="MeasuredRates"/>, y releer el archivo: true = graba y se decodifica entero. El codificador abre solo
    /// sus frecuencias (<see cref="EncoderOpens"/>); además, el muxer MXF solo lleva 48 kHz y el MP4 no lleva MP3 por debajo de
    /// 16 kHz (el MOV sí). MP2, con 128 kb/s, que vale a todas sus frecuencias (<see cref="Mp2_TakesOnlyTheBitratesOfItsMpegVersion"/>).
    /// </summary>
    private static bool RecordsAt(AudioCodec audio, ContainerFormat container, int rate)
    {
        var encoded = Encoded(audio, container);
        bool carried = container switch
        {
            ContainerFormat.Mxf => rate == 48000,
            ContainerFormat.Mp4 when encoded is AudioCodec.Mp3 => rate >= 16000,
            _ => true,
        };
        return EncoderOpens(encoded, rate) && carried;
    }

    /// <summary>Ese audio en ese contenedor a esa frecuencia; MP2 a 128 kb/s, que vale a 16–48 kHz: aquí solo cuenta la frecuencia.</summary>
    private static RecordingProfile WithAudioAt(AudioCodec audio, ContainerFormat container, bool audioOnly, int rate)
    {
        var p = WithAudio(audio, container, audioOnly);
        p.AudioSampleRate = rate;
        if (audio is AudioCodec.Mp2) p.AudioBitrate = Bitrate.FromKbps(128);
        return p;
    }

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void SampleRates_AreRejected_ExactlyWhereTheEngineCannotRecordThem(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        // A 48 kHz graban todos los audios en todos los contenedores que los llevan: lo que salga ahí es de otra regla (el contenedor
        // no lleva ese audio, MXF necesita vídeo) y no cambia con la frecuencia.
        var others = RecordingProfileValidator.Validate(WithAudioAt(audio, container, audioOnly, 48000));
        Assert.DoesNotContain(others, e => e.Contains(" Hz", StringComparison.Ordinal));
        bool carried = MeasuredAudio(audio, container) == AudioOutcome.Records;

        foreach (int rate in MeasuredRates)
        {
            var errors = RecordingProfileValidator.Validate(WithAudioAt(audio, container, audioOnly, rate));
            var added = errors.Except(others).ToList();
            if (!carried || RecordsAt(audio, container, rate))
            {
                // Si el contenedor no lleva ese audio, el mensaje es ese (y propone otro audio): la frecuencia no añade nada.
                Assert.True(added.Count == 0, $"{audio} en {container} a {rate} Hz: {string.Join(" | ", added)}");
                continue;
            }

            string message = Assert.Single(added);
            var encoded = Encoded(audio, container);
            if (!EncoderOpens(encoded, rate) && encoded == audio)
                Assert.StartsWith($"El audio {audio} no se puede grabar a {rate} Hz: FFmpeg no admite esa frecuencia con {audio} ", message);
            else // el contenedor no la lleva, o ahí el PCM es AAC
                Assert.StartsWith($"El audio {audio} no se puede grabar a {rate} Hz en {container}: FFmpeg no admite esa combinación ", message);
            Assert.Contains("la grabación no arrancaría", message);
        }
    }

    [Theory]
    [MemberData(nameof(EveryAudioCodecInEveryContainer))]
    public void TheRatesTheMessageSuggests_AreEveryRateThatRecords(AudioCodec audio, ContainerFormat container, bool audioOnly)
    {
        Localizer.Language = AppLanguage.English;
        foreach (int rate in MeasuredRates)
        {
            string? message = RecordingProfileValidator.Validate(WithAudioAt(audio, container, audioOnly, rate))
                .SingleOrDefault(e => e.StartsWith($"{audio} audio can't be recorded at {rate} Hz", StringComparison.Ordinal));
            if (message is null) continue;

            // «… With Opus in Mkv, choose 8000, 12000, 16000, 24000 or 48000 Hz.»: de menor a mayor, todas las que graban (de las
            // medidas) y ninguna más; con cada una, la frecuencia deja de ser un problema.
            int from = message.LastIndexOf("choose ", StringComparison.Ordinal) + "choose ".Length;
            var suggested = message[from..message.LastIndexOf(" Hz.", StringComparison.Ordinal)]
                .Replace(" or ", ", ", StringComparison.Ordinal).Split(", ").Select(int.Parse).ToList();
            Assert.Equal(MeasuredRates.Where(r => RecordsAt(audio, container, r)), suggested);
            foreach (int alternative in suggested)
                Assert.DoesNotContain(RecordingProfileValidator.Validate(WithAudioAt(audio, container, audioOnly, alternative)),
                    e => e.Contains(" Hz", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void OpusAt44100_SaysWhyAndWhichRatesToChoose_InSpanish()
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Opus, ContainerFormat.Mkv, false, 44100)));
        Assert.Equal("El audio Opus no se puede grabar a 44100 Hz: FFmpeg no admite esa frecuencia con Opus y la grabación no " +
                     "arrancaría. Con Opus en Mkv, elige 8000, 12000, 16000, 24000 o 48000 Hz.", error);
    }

    [Fact]
    public void MxfAt44100_SaysOnly48000_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Pcm, ContainerFormat.Mxf, false, 44100)));
        Assert.Equal("Pcm audio can't be recorded at 44100 Hz in Mxf: FFmpeg doesn't support that combination, so the recording " +
                     "wouldn't start. With Pcm in Mxf, choose 48000 Hz.", error);
    }

    [Fact]
    public void PcmInMp4_TakesTheRatesOfAac_BecauseTheEngineRecordsItAsAac()
    {
        // En MKV el PCM graba a 192 kHz; en MP4 es AAC, que no pasa de 96 kHz.
        Assert.Empty(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Pcm, ContainerFormat.Mkv, false, 192000)));
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Pcm, ContainerFormat.Mp4, false, 192000)));
        Assert.Equal("El audio Pcm no se puede grabar a 192000 Hz en Mp4: FFmpeg no admite esa combinación y la grabación no " +
                     "arrancaría. Con Pcm en Mp4, elige 7350, 8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, " +
                     "88200 o 96000 Hz.", error);
    }

    [Fact]
    public void Mp3InMp4_Below16000_IsRejected_ButNotInMov()
    {
        // El muxer MP4 lo rechaza («muxing mp3 at 8000hz is not standard, to mux anyway set strict to -1»); el MOV lo lleva.
        Assert.Empty(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Mp3, ContainerFormat.Mov, false, 8000)));
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Mp3, ContainerFormat.Mp4, false, 8000)));
        Assert.Equal("El audio Mp3 no se puede grabar a 8000 Hz en Mp4: FFmpeg no admite esa combinación y la grabación no " +
                     "arrancaría. Con Mp3 en Mp4, elige 16000, 22050, 24000, 32000, 44100 o 48000 Hz.", error);
    }

    [Fact]
    public void AnAudioTheContainerDoesNotCarry_GetsNoRateMessage()
    {
        // Opus no va en MOV a ninguna frecuencia: el mensaje propone otro audio (AAC, que sí graba a 44,1 kHz).
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Opus, ContainerFormat.Mov, false, 44100)));
        Assert.StartsWith("El audio Opus no se puede grabar en Mov: ", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-44100)]
    public void ARateThatIsNotPositive_OnlySaysSo(int rate)
    {
        var error = Assert.Single(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Opus, ContainerFormat.Mkv, false, rate)));
        Assert.Equal("La frecuencia de muestreo de audio debe ser mayor que cero.", error);
    }

    [Theory]
    [InlineData(ContainerFormat.Mkv, false)]
    [InlineData(ContainerFormat.Avi, false)]
    [InlineData(ContainerFormat.Wav, true)]
    public void Pcm_RecordsAtAnyRate_OutsideMxfAndTheContainersWhereItIsAac(ContainerFormat container, bool audioOnly)
    {
        foreach (int rate in new[] { 7350, 8000, 44100, 47952, 48048, 96000, 176400, 192000, 352800, 384000, 768000 })
            Assert.Empty(RecordingProfileValidator.Validate(WithAudioAt(AudioCodec.Pcm, container, audioOnly, rate)));
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(22050)]
    [InlineData(24000)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Mp2_TakesOnlyTheBitratesOfItsMpegVersion(int rate)
    {
        // Medido con el motor (8.0 y el incluido): a 24 kHz (MPEG-2, LSF) graba toda la tabla LSF y no abre con 100, 192, 224, 256,
        // 320 ni 384 kb/s («bitrate 256 is not allowed in mp2»); a 48 kHz (MPEG-1), toda la suya, y no con 8, 16, 24, 40, 144, 200,
        // 300 ni 400; a 16, 22,05, 32 y 44,1 kHz, lo mismo en lo que se midió. Los presets de fábrica con MP2 (256 y 384 kb/s a
        // 48 kHz) graban.
        int[] table = rate is 16000 or 22050 or 24000
            ? new[] { 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }
            : new[] { 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
        for (int kbps = 1; kbps <= 448; kbps++)
        {
            var p = WithAudioAt(AudioCodec.Mp2, ContainerFormat.Mkv, audioOnly: false, rate);
            p.AudioBitrate = Bitrate.FromKbps(kbps);
            var errors = RecordingProfileValidator.Validate(p);
            if (table.Contains(kbps))
                Assert.True(errors.Count == 0, $"{kbps} kb/s a {rate} Hz: {string.Join(" | ", errors)}");
            else
                Assert.StartsWith($"El audio Mp2 a {rate} Hz no se puede grabar a {kbps} kb/s: ", Assert.Single(errors));
        }
    }

    [Fact]
    public void Mp2At24000With256_SaysWhichBitratesToChoose_InSpanish()
    {
        // El bitrate de los presets de MP2 (y el del editor): a 24 kHz no abre.
        var p = WithAudioAt(AudioCodec.Mp2, ContainerFormat.ProgramStream, audioOnly: false, 24000);
        p.AudioBitrate = Bitrate.FromKbps(256);
        Assert.Equal("El audio Mp2 a 24000 Hz no se puede grabar a 256 kb/s: FFmpeg no admite esa combinación y la grabación no " +
                     "arrancaría. A 24000 Hz, elige 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144 o 160 kb/s.",
                     Assert.Single(RecordingProfileValidator.Validate(p)));
    }

    [Fact]
    public void Mp2At48000With200_SaysWhichBitratesToChoose_InEnglish()
    {
        Localizer.Language = AppLanguage.English;
        var p = WithAudioAt(AudioCodec.Mp2, ContainerFormat.Ts, audioOnly: false, 48000);
        p.AudioBitrate = Bitrate.FromKbps(200);
        Assert.Equal("Mp2 audio at 48000 Hz can't be recorded at 200 kb/s: FFmpeg doesn't support that combination, so the " +
                     "recording wouldn't start. At 48000 Hz, choose 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 or " +
                     "384 kb/s.", Assert.Single(RecordingProfileValidator.Validate(p)));
    }

    [Fact]
    public void TheRateTables_AreTheOnesOfTheEncodersTheEngineUses()
    {
        // Las frecuencias son las de cada codificador de FFmpeg: si el motor cambiara de codificador (el FDK para «Aac», p. ej.),
        // esta prueba avisa de que las tablas del validador ya no valen.
        Assert.Equal("pcm_s24le", FfmpegCodecMap.AudioEncoder(AudioCodec.Pcm));
        Assert.Equal("aac", FfmpegCodecMap.AudioEncoder(AudioCodec.Aac));
        Assert.Equal("libfdk_aac", FfmpegCodecMap.AudioEncoder(AudioCodec.FdkAac));
        Assert.Equal("libopus", FfmpegCodecMap.AudioEncoder(AudioCodec.Opus));
        Assert.Equal("libmp3lame", FfmpegCodecMap.AudioEncoder(AudioCodec.Mp3));
        Assert.Equal("mp2", FfmpegCodecMap.AudioEncoder(AudioCodec.Mp2));
        foreach (var container in Enum.GetValues<ContainerFormat>())
            Assert.Equal(Encoded(AudioCodec.Pcm, container) is AudioCodec.Aac ? "aac" : "pcm_s24le",
                FfmpegCodecMap.EffectiveAudioEncoder(AudioCodec.Pcm, container));
    }

    // --- Cadencia ---

    private static RecordingProfile At(int numerator, int denominator, VideoCodec codec = VideoCodec.H264x264, ContainerFormat container = ContainerFormat.Mp4)
    {
        var p = With(codec, container);
        p.OutputFrameRate = new FrameRate(numerator, denominator);
        return p;
    }

    [Theory]
    [InlineData(25, 1001)] // 0,025: lo que dejaba el editor en la copia de un preset 29.97 al escribir 25 (cambiaba solo el numerador)
    [InlineData(1, 2)]
    [InlineData(0, 1)]
    [InlineData(25, 0)]
    [InlineData(121, 1)]
    [InlineData(2500, 1)]  // 25 con dos ceros de más
    public void AFrameRateOutsideOneTo120_IsRejected(int numerator, int denominator)
        => Assert.StartsWith("La cadencia ", Assert.Single(RecordingProfileValidator.Validate(At(numerator, denominator))));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24000, 1001)]
    [InlineData(25, 1)]
    [InlineData(30000, 1001)]
    [InlineData(50, 1)]
    [InlineData(60000, 1001)]
    [InlineData(120, 1)]
    [InlineData(25, 2)]
    public void AFrameRateFromOneTo120_IsAccepted(int numerator, int denominator)
        => Assert.Empty(RecordingProfileValidator.Validate(At(numerator, denominator)));

    [Fact]
    public void AFrameRateOf25Over1001_SaysWhatToType()
        => Assert.Equal("La cadencia 25/1001 no es válida: tiene que estar entre 1 y 120 cuadros por segundo (25, 29.97, 50, 59.94…), " +
                        "o vacía para grabar la de la fuente.", Assert.Single(RecordingProfileValidator.Validate(At(25, 1001))));

    [Theory]
    [InlineData(VideoCodec.Mpeg2Video, 15, 1)]
    [InlineData(VideoCodec.Mpeg2Video, 10, 1)]
    [InlineData(VideoCodec.H264x264, 12, 1)]
    [InlineData(VideoCodec.ProRes, 1, 1)]
    [InlineData(VideoCodec.DnxHr, 110, 1)]
    public void Mxf_RefusesTheFrameRatesItsMuxerDoesNotKnow(VideoCodec codec, int numerator, int denominator)
        => Assert.StartsWith($"No se puede grabar a {numerator} cuadros por segundo en Mxf: el contenedor no admite esa cadencia",
            Assert.Single(RecordingProfileValidator.Validate(At(numerator, denominator, codec, ContainerFormat.Mxf))));

    [Fact]
    public void Mxf_SaysWhichFrameRatesToChoose()
        => Assert.Equal("No se puede grabar a 15 cuadros por segundo en Mxf: el contenedor no admite esa cadencia y la grabación no " +
                        "arrancaría. En Mxf, elige 23.976 o 24.",
            Assert.Single(RecordingProfileValidator.Validate(At(15, 1, VideoCodec.Mpeg2Video, ContainerFormat.Mxf))));

    [Theory]
    [InlineData(24000, 1001)] [InlineData(24, 1)] [InlineData(25, 1)] [InlineData(30000, 1001)] [InlineData(30, 1)]
    [InlineData(48, 1)] [InlineData(50, 1)] [InlineData(60000, 1001)] [InlineData(60, 1)] [InlineData(100, 1)]
    [InlineData(120000, 1001)] [InlineData(120, 1)]
    public void Mxf_TakesTheBroadcastFrameRates(int numerator, int denominator)
    {
        foreach (var codec in new[] { VideoCodec.Mpeg2Video, VideoCodec.H264x264, VideoCodec.ProRes, VideoCodec.DnxHr })
        {
            var errors = RecordingProfileValidator.Validate(At(numerator, denominator, codec, ContainerFormat.Mxf));
            // 119.88 sí lo lleva el MXF, pero MPEG-2 no lo codifica.
            if (codec is VideoCodec.Mpeg2Video && numerator == 120000)
                Assert.StartsWith("MPEG-2 no codifica a 119.88 cuadros por segundo", Assert.Single(errors));
            else
                Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData(13, 1, "12 o 15")]
    [InlineData(26, 1, "24 o 25")]
    [InlineData(55, 1, "50 o 59.94")]
    [InlineData(119, 1, "100 o 120")]
    public void Mpeg2_RefusesTheFrameRatesItCannotEncode_AndSaysTheNearest(int numerator, int denominator, string nearest)
        => Assert.Equal($"MPEG-2 no codifica a {numerator} cuadros por segundo: FFmpeg lo grabaría sin avisar a otra cadencia. Con " +
                        $"MPEG-2, elige {nearest}.",
            Assert.Single(RecordingProfileValidator.Validate(At(numerator, denominator, VideoCodec.Mpeg2Video, ContainerFormat.Ts))));

    [Theory]
    [InlineData(12, 1)] [InlineData(15, 1)] [InlineData(24000, 1001)] [InlineData(25, 1)] [InlineData(30000, 1001)]
    [InlineData(48, 1)] [InlineData(50, 1)] [InlineData(60000, 1001)] [InlineData(100, 1)] [InlineData(120, 1)]
    public void Mpeg2_TakesTheFrameRatesItEncodes(int numerator, int denominator)
        => Assert.Empty(RecordingProfileValidator.Validate(At(numerator, denominator, VideoCodec.Mpeg2Video, ContainerFormat.Ts)));

    [Fact]
    public void EveryFactoryPresetFrameRate_IsAccepted()
    {
        foreach (var preset in Baioss.Record.Application.Presets.PresetCatalog.CreateBuiltIns())
            Assert.True(RecordingProfileValidator.Validate(preset.ToProfile()).Count == 0, preset.Name);
    }

    // --- Formato de píxel, perfil intra, barrido y tamaño ---

    private static RecordingProfile Video(VideoCodec codec, PixelFormat pixel = PixelFormat.Auto, EncoderProfile profile = EncoderProfile.Auto,
        ScanType scan = ScanType.Progressive, Resolution? size = null)
    {
        var container = codec switch
        {
            VideoCodec.ProRes or VideoCodec.DnxHd or VideoCodec.DnxHr => ContainerFormat.Mov,
            VideoCodec.Mpeg2Video => ContainerFormat.Ts,
            _ => ContainerFormat.Mp4,
        };
        var p = With(codec, container);
        p.PixelFormat = pixel;
        p.EncoderProfile = profile;
        p.ScanType = scan;
        p.TargetResolution = size ?? new Resolution(1920, 1080);
        return p;
    }

    [Theory]
    // Medido con el motor y FFmpeg 8.0: lo que el codificador no admite, FFmpeg lo cambia sin avisar por el más parecido.
    [InlineData(VideoCodec.Mpeg2Video, PixelFormat.Yuv420p10le, "Auto, Yuv420p o Yuv422p")]  // sale en yuv420p
    [InlineData(VideoCodec.Mpeg2Video, PixelFormat.Yuv444p10le, "Auto, Yuv420p o Yuv422p")]
    [InlineData(VideoCodec.H264Qsv, PixelFormat.Yuv422p10le, "Auto o Yuv420p")]            // sale en yuv420p
    [InlineData(VideoCodec.H264Qsv, PixelFormat.Yuv444p10le, "Auto o Yuv420p")]
    [InlineData(VideoCodec.ProRes, PixelFormat.Yuv420p10le, "Auto, Yuv422p10le o Yuv444p10le")] // sale en yuv422p10le
    [InlineData(VideoCodec.ProRes, PixelFormat.Yuv422p, "Auto, Yuv422p10le o Yuv444p10le")]
    [InlineData(VideoCodec.DnxHr, PixelFormat.Yuv420p, "Auto, Yuv422p, Yuv422p10le o Yuv444p10le")]  // sale en yuv422p
    [InlineData(VideoCodec.DnxHd, PixelFormat.Yuv420p10le, "Auto, Yuv422p, Yuv422p10le o Yuv444p10le")]
    public void APixelFormatTheCodecDoesNotEncode_IsRejected(VideoCodec codec, PixelFormat pixel, string choose)
        => Assert.Equal($"{codec} no codifica en {pixel}: la grabación saldría en otro formato de píxel sin avisar. Con {codec}, elige {choose}.",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, pixel))));

    [Theory]
    [InlineData(VideoCodec.H264x264)]
    [InlineData(VideoCodec.H265x265)]
    [InlineData(VideoCodec.H264Nvenc)] // por hardware: lo que la tarjeta no hace degrada a CPU, que lo graba
    [InlineData(VideoCodec.HevcNvenc)]
    [InlineData(VideoCodec.HevcQsv)]
    public void EveryPixelFormat_IsAccepted_WhereTheCodecOrItsFallbackEncodesIt(VideoCodec codec)
    {
        foreach (var pixel in Enum.GetValues<PixelFormat>())
            Assert.Empty(RecordingProfileValidator.Validate(Video(codec, pixel)));
    }

    [Theory]
    [InlineData(VideoCodec.DnxHr, EncoderProfile.ProRes4444Xq, "ProRes", "DNxHR")]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.ProResHq, "ProRes", "DNxHR")]
    [InlineData(VideoCodec.ProRes, EncoderProfile.DnxHrHq, "DNxHR", "ProRes")]
    public void AnIntraProfileOfTheOtherFamily_IsRejected(VideoCodec codec, EncoderProfile profile, string family, string own)
        => Assert.Equal($"El perfil {profile} es de {family}: con {codec} no se usa y la grabación no saldría con él. Elige un perfil de {own} o Auto.",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, profile: profile))));

    [Theory]
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrHq, PixelFormat.Yuv422p10le, PixelFormat.Yuv422p)]  // «pixel format is incompatible»
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrHqx, PixelFormat.Yuv422p, PixelFormat.Yuv422p10le)]
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHr444, PixelFormat.Yuv422p, PixelFormat.Yuv444p10le)]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.DnxHrHqx, PixelFormat.Yuv422p, PixelFormat.Yuv422p10le)]
    [InlineData(VideoCodec.ProRes, EncoderProfile.ProRes4444, PixelFormat.Yuv422p10le, PixelFormat.Yuv444p10le)]
    [InlineData(VideoCodec.ProRes, EncoderProfile.ProResHq, PixelFormat.Yuv444p10le, PixelFormat.Yuv422p10le)]
    public void AnIntraProfileThatContradictsThePixelFormat_IsRejected(VideoCodec codec, EncoderProfile profile, PixelFormat pixel, PixelFormat wanted)
        => Assert.Equal($"El perfil {profile} graba en {wanted}, no en {pixel}: FFmpeg no abriría el codificador o grabaría el del perfil sin avisar. " +
                        $"Elige {wanted} o deja el formato de píxel en Auto.",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, pixel, profile))));

    [Fact]
    public void AnIntraProfileWithItsPixelFormat_OrWithAuto_IsAccepted()
    {
        foreach (var profile in new[] { EncoderProfile.ProResProxy, EncoderProfile.ProResLt, EncoderProfile.ProResStandard, EncoderProfile.ProResHq,
                     EncoderProfile.ProRes4444, EncoderProfile.ProRes4444Xq })
        {
            Assert.Empty(RecordingProfileValidator.Validate(Video(VideoCodec.ProRes, PixelFormat.Auto, profile)));
            var pixel = profile is EncoderProfile.ProRes4444 or EncoderProfile.ProRes4444Xq ? PixelFormat.Yuv444p10le : PixelFormat.Yuv422p10le;
            Assert.Empty(RecordingProfileValidator.Validate(Video(VideoCodec.ProRes, pixel, profile)));
        }
        foreach (var (profile, pixel) in new[] { (EncoderProfile.DnxHrLb, PixelFormat.Yuv422p), (EncoderProfile.DnxHrSq, PixelFormat.Yuv422p),
                     (EncoderProfile.DnxHrHq, PixelFormat.Yuv422p), (EncoderProfile.DnxHrHqx, PixelFormat.Yuv422p10le), (EncoderProfile.DnxHr444, PixelFormat.Yuv444p10le) })
        {
            Assert.Empty(RecordingProfileValidator.Validate(Video(VideoCodec.DnxHr, PixelFormat.Auto, profile)));
            Assert.Empty(RecordingProfileValidator.Validate(Video(VideoCodec.DnxHr, pixel, profile)));
        }
        // En un códec que no es intra el perfil no se usa: un ProRes que quedó al pasar el preset a x264 no impide grabar.
        Assert.Empty(RecordingProfileValidator.Validate(Video(VideoCodec.H264x264, profile: EncoderProfile.ProRes4444Xq)));
    }

    [Theory]
    [InlineData(VideoCodec.H264Nvenc)] // declara los cuadros progresivos: peine en lo que se mueve
    [InlineData(VideoCodec.HevcNvenc)]
    [InlineData(VideoCodec.Av1Nvenc)]
    [InlineData(VideoCodec.H264Qsv)]
    [InlineData(VideoCodec.HevcQsv)]
    [InlineData(VideoCodec.H264Amf)]
    [InlineData(VideoCodec.H265x265)]
    public void AnEncoderThatDoesNotInterlace_IsRejectedForAnInterlacedPreset(VideoCodec codec)
    {
        foreach (var scan in new[] { ScanType.InterlacedTff, ScanType.InterlacedBff })
            Assert.Equal($"{codec} no codifica entrelazado: guardaría los dos campos en cuadros progresivos, con peine en todo lo que se mueve. " +
                         $"Para {scan}, elige H264x264, Mpeg2Video, ProRes o DnxHd; con {codec}, un barrido progresivo.",
                Assert.Single(RecordingProfileValidator.Validate(Video(codec, scan: scan))));
    }

    [Theory]
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrLb, PixelFormat.Auto, 1920, 1080)]      // LB no tiene DNxHD equivalente
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHr444, PixelFormat.Auto, 1920, 1080)]     // ni 4:4:4
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv444p10le, 1920, 1080)]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv422p, 720, 576)]        // DNxHD no tiene SD
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrHq, PixelFormat.Auto, 1280, 720)]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv422p10le, 1440, 1080)]  // 1440×1080i solo en 8 bits
    public void ADnxThatHasNoInterlacedCid_IsRejectedForAnInterlacedPreset(VideoCodec codec, EncoderProfile profile, PixelFormat pixel, int w, int h)
        => Assert.StartsWith($"Este {codec} no se puede grabar entrelazado: el DNxHD entrelazado solo existe a 1920x1080",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, pixel, profile, ScanType.InterlacedTff, new Resolution(w, h)))));

    [Theory]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv422p, 1920, 1080)]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv422p10le, 1920, 1080)]
    [InlineData(VideoCodec.DnxHd, EncoderProfile.Auto, PixelFormat.Yuv422p, 1440, 1080)]
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrSq, PixelFormat.Auto, 1920, 1080)]  // se graba como su DNxHD
    [InlineData(VideoCodec.DnxHr, EncoderProfile.DnxHrHqx, PixelFormat.Auto, 1920, 1080)]
    public void ADnxWithAnInterlacedCid_IsAcceptedForAnInterlacedPreset(VideoCodec codec, EncoderProfile profile, PixelFormat pixel, int w, int h)
        => Assert.Empty(RecordingProfileValidator.Validate(Video(codec, pixel, profile, ScanType.InterlacedTff, new Resolution(w, h))));

    [Theory]
    [InlineData(VideoCodec.H264x264, PixelFormat.Auto, 578, "576 o 580")]   // x264: «height not divisible by 4 (720x578)»
    [InlineData(VideoCodec.H264x264, PixelFormat.Yuv420p10le, 486, "484 o 488")]
    [InlineData(VideoCodec.Mpeg2Video, PixelFormat.Yuv420p, 1082, "1080 o 1084")]
    public void AnInterlaced420Preset_NeedsAHeightThatIsAMultipleOf4(VideoCodec codec, PixelFormat pixel, int height, string choose)
        => Assert.Equal($"Un entrelazado en 4:2:0 necesita un alto múltiplo de 4 (cada campo con un número par de líneas): a {height} líneas no " +
                        $"se grabaría bien. Elige {choose}.",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, pixel, scan: ScanType.InterlacedTff, size: new Resolution(720, height)))));

    [Theory]
    [InlineData(VideoCodec.Mpeg2Video, PixelFormat.Yuv422p, 486)] // en 4:2:2, cada campo lleva todas sus líneas de croma
    [InlineData(VideoCodec.ProRes, PixelFormat.Auto, 578)]
    [InlineData(VideoCodec.H264x264, PixelFormat.Yuv422p, 486)]
    public void AnInterlaced422Preset_TakesAnyEvenHeight(VideoCodec codec, PixelFormat pixel, int height)
        => Assert.Empty(RecordingProfileValidator.Validate(Video(codec, pixel, scan: ScanType.InterlacedTff, size: new Resolution(720, height))));

    [Theory]
    [InlineData(4096, 2160)] // DCI 4K: «Width or Height are not allowed to be multiples of 4096»
    [InlineData(8192, 4320)]
    [InlineData(1920, 4096)]
    public void Mpeg2_RefusesAWidthOrHeightThatIsAMultipleOf4096(int w, int h)
        => Assert.StartsWith($"MPEG-2 no codifica a {w}x{h}: no admite un ancho ni un alto múltiplo de 4096",
            Assert.Single(RecordingProfileValidator.Validate(Video(VideoCodec.Mpeg2Video, size: new Resolution(w, h)))));

    [Theory]
    [InlineData(VideoCodec.DnxHr, 160, 90)] // «Input dimensions too small, input must be at least 256x120»
    [InlineData(VideoCodec.DnxHr, 254, 720)]
    [InlineData(VideoCodec.DnxHd, 320, 118)]
    public void Dnx_RefusesASizeBelow256x120(VideoCodec codec, int w, int h)
        => Assert.Equal($"{codec} no codifica a {w}x{h}: el tamaño mínimo es 256x120, el codificador no abriría y la grabación no arrancaría. " +
                        "Elige un tamaño mayor o el de la fuente.",
            Assert.Single(RecordingProfileValidator.Validate(Video(codec, size: new Resolution(w, h)))));

    // --- Canales y bitrate del audio ---

    [Theory]
    [InlineData(AudioCodec.Mp2, ContainerFormat.Ts, AudioLayout.Surround51)]
    [InlineData(AudioCodec.Mp2, ContainerFormat.ProgramStream, AudioLayout.Surround71)]
    [InlineData(AudioCodec.Mp3, ContainerFormat.Mp4, AudioLayout.Surround51)]
    [InlineData(AudioCodec.Mp3, ContainerFormat.Avi, AudioLayout.Surround71)]
    public void Mp2AndMp3_OnlyCarryMonoOrStereo(AudioCodec audio, ContainerFormat container, AudioLayout layout)
    {
        // Medido con el motor y FFmpeg 8.0: MP2 en 7.1 no abre («Error while opening encoder») y la grabación no arranca.
        var p = WithAudio(audio, container, audioOnly: false);
        p.AudioLayout = layout;
        p.AudioBitrate = Bitrate.FromKbps(192);
        Assert.Equal($"El audio {audio} no se puede grabar en {layout}: solo lleva Mono o Stereo, el codificador no abriría y la grabación " +
                     $"no arrancaría. Con {audio}, elige Mono o Stereo; para {layout}, el audio Aac o Pcm.",
                     Assert.Single(RecordingProfileValidator.Validate(p)));
        // Con varias pistas (los pares de una fuente multicanal) cada una es estéreo: se graba.
        p.AudioTracks = AudioTrackMode.PairsAsTracks;
        Assert.Empty(RecordingProfileValidator.Validate(p));
    }

    [Theory]
    [InlineData(AudioLayout.Mono, 256, true)]
    [InlineData(AudioLayout.Mono, 320, false)]
    [InlineData(AudioLayout.Stereo, 512, true)]
    [InlineData(AudioLayout.Stereo, 520, false)]
    [InlineData(AudioLayout.Surround51, 1536, true)]
    [InlineData(AudioLayout.Surround51, 1600, false)] // «The bit rate 1600000 bps is unsupported. Please choose a value between 500 and 1536000»
    [InlineData(AudioLayout.Surround71, 2048, true)]
    public void Opus_TakesUpTo256KbpsPerChannel(AudioLayout layout, int kbps, bool records)
    {
        var p = WithAudio(AudioCodec.Opus, ContainerFormat.Mkv, audioOnly: false);
        p.AudioLayout = layout;
        p.AudioBitrate = Bitrate.FromKbps(kbps);
        var errors = RecordingProfileValidator.Validate(p);
        if (records) Assert.Empty(errors);
        else Assert.StartsWith($"El audio Opus no se puede grabar a {kbps} kb/s con ", Assert.Single(errors));
    }
}
