using Baioss.Record.App.Review;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.App.Tests;

/// <summary>
/// La salida de audio de «Revisar» en el equipo donde corren las pruebas: nunca una DeckLink (en el equipo de pruebas la salida
/// por defecto de Windows ES una DeckLink, la 4). Solo se abre y se cierra: no se escribe sonido.
/// </summary>
public sealed class ReviewAudioOutputTests
{
    private readonly ITestOutputHelper _out;
    public ReviewAudioOutputTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheReviewSound_NeverOpensADeckLinkOutput()
    {
        var outputs = WaveOutAudioOutput.Outputs();
        foreach (var o in outputs)
            _out.WriteLine($"{o.Id}: «{o.Name}» · adaptador «{o.Adapter}»{(o.IsDefault ? " · POR DEFECTO" : "")}{(o.IsDeckLink ? " · DeckLink" : "")}");

        var (output, sound, name) = WaveOutAudioOutput.Open();
        using (output)
        {
            _out.WriteLine($"elegida: {name ?? "(ninguna)"} · {sound}");
            if (output is not null)
            {
                var chosen = Assert.Single(outputs, o => o.Name == name);
                Assert.False(chosen.IsDeckLink, $"«{name}» es de una DeckLink");
            }
            if (outputs.Any(o => o.IsDefault && o.IsDeckLink))
            {
                // Con la DeckLink por defecto suena por otra (o por ninguna, si esa otra no abre), y la ventana lo dice.
                if (outputs.Any(o => !o.IsDeckLink)) Assert.True(sound is ReviewSound.OtherOutput or ReviewSound.NoOutput, sound.ToString());
                else Assert.Equal(ReviewSound.OnlyDeckLink, sound);
            }
        }
    }
}
