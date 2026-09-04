using System.Globalization;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Localization;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Catálogo de traducciones. Lo que de verdad hay que impedir aquí es publicar una versión A MEDIO traducir:
/// que una ventana aparezca en español dentro de la interfaz en inglés porque alguien añadió una cadena y se
/// olvidó del otro idioma.
/// </summary>
public sealed class LocalizationTests
{
    [Fact]
    public void BothLanguages_HaveExactlyTheSameKeys()
    {
        var missingInEnglish = Strings.Spanish.Keys.Where(k => !Strings.English.ContainsKey(k)).OrderBy(k => k).ToList();
        var missingInSpanish = Strings.English.Keys.Where(k => !Strings.Spanish.ContainsKey(k)).OrderBy(k => k).ToList();

        Assert.True(missingInEnglish.Count == 0, "Faltan en INGLÉS: " + string.Join(", ", missingInEnglish));
        Assert.True(missingInSpanish.Count == 0, "Faltan en ESPAÑOL: " + string.Join(", ", missingInSpanish));
    }

    [Fact]
    public void PlaceholdersMatch_SoFormattingNeverBreaks()
    {
        // Si el español dice «{0} canales» y el inglés se deja el {0}, al formatear se pierde el número (o
        // peor, se lanza FormatException con un índice que no existe). Se comparan los marcadores usados.
        var mismatches = new List<string>();
        foreach (var (key, spanish) in Strings.Spanish)
        {
            var english = Strings.English[key];
            var inEs = Placeholders(spanish);
            var inEn = Placeholders(english);
            if (!inEs.SetEquals(inEn))
                mismatches.Add($"{key}: es={{{string.Join(",", inEs.Order())}}} en={{{string.Join(",", inEn.Order())}}}");
        }
        Assert.True(mismatches.Count == 0, "Marcadores distintos entre idiomas:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void NoEmptyTranslations()
    {
        var empty = Strings.Spanish.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "es:" + p.Key)
            .Concat(Strings.English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "en:" + p.Key))
            .ToList();
        Assert.True(empty.Count == 0, "Cadenas vacías: " + string.Join(", ", empty));
    }

    [Fact]
    public void UntranslatedKey_FallsBackToSpanish_InsteadOfShowingNothing()
    {
        var previous = Localizer.Language;
        try
        {
            Localizer.Language = AppLanguage.English;
            // Clave inexistente: se devuelve la propia clave, nunca vacío ni excepción.
            Assert.Equal("Clave_Que_No_Existe", Localizer.T("Clave_Que_No_Existe"));
            Assert.Equal("", Localizer.T(""));
        }
        finally { Localizer.Language = previous; }
    }

    [Fact]
    public void SwitchingLanguage_ChangesTheText()
    {
        var previous = Localizer.Language;
        try
        {
            Localizer.Language = AppLanguage.Spanish;
            var es = Localizer.T("Ch_Record");
            Localizer.Language = AppLanguage.English;
            var en = Localizer.T("Ch_Record");

            Assert.Equal("● Grabar", es);
            Assert.Equal("● Record", en);
        }
        finally { Localizer.Language = previous; }
    }

    [Theory]
    [InlineData("es-ES", AppLanguage.Spanish)]
    [InlineData("es-MX", AppLanguage.Spanish)]
    [InlineData("es-GT", AppLanguage.Spanish)]   // el equipo del usuario
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("en-GB", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]   // idioma no soportado → inglés
    [InlineData("pt-BR", AppLanguage.English)]
    public void SystemLanguage_PicksSpanishForAnySpanishVariant(string culture, AppLanguage expected)
        => Assert.Equal(expected, Localizer.FromSystem(new CultureInfo(culture)));

    [Fact]
    public void Plural_PicksTheRightVariant_SoThereIsNo1Channels()
    {
        var previous = Localizer.Language;
        try
        {
            Localizer.Language = AppLanguage.Spanish;
            Assert.Equal("Licencia activa · 1 canal",
                Localizer.Plural("Lic_Summary_LicensedChannels_One", "Lic_Summary_LicensedChannels_Many", 1));
            Assert.Equal("Licencia activa · 4 canales",
                Localizer.Plural("Lic_Summary_LicensedChannels_One", "Lic_Summary_LicensedChannels_Many", 4));

            Localizer.Language = AppLanguage.English;
            Assert.Equal("License active · 1 channel",
                Localizer.Plural("Lic_Summary_LicensedChannels_One", "Lic_Summary_LicensedChannels_Many", 1));
            Assert.Equal("License active · 4 channels",
                Localizer.Plural("Lic_Summary_LicensedChannels_One", "Lic_Summary_LicensedChannels_Many", 4));
        }
        finally { Localizer.Language = previous; }
    }

    private static HashSet<string> Placeholders(string text)
        => Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();
}
