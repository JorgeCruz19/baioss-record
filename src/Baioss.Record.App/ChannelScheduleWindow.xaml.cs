using System.Collections.Generic;
using System.Windows;

namespace Baioss.Record.App;

/// <summary>
/// Ventana de solo lectura con la programación de HOY de un canal: una tabla con Entrada · Salida ·
/// Título · Segmento. La abre el botón «Mostrar programación» del panel; el panel solo enseña la
/// grabación en curso. La fila EN CURSO va resaltada (verde), igual que en el panel.
/// </summary>
public partial class ChannelScheduleWindow : Window
{
    public ChannelScheduleWindow(string channelKey, IReadOnlyList<TodayTaskRow> rows)
    {
        InitializeComponent();
        HeaderText = Localization.Loc.F("Sch_Ch_HeaderToday", channelKey);
        Title = Localization.Loc.F("Sch_Ch_WindowTitle", channelKey);
        Rows = rows;
        HasRows = rows.Count > 0;
        NoRows = rows.Count == 0;
        DataContext = this;
    }

    public string HeaderText { get; }
    public IReadOnlyList<TodayTaskRow> Rows { get; }
    public bool HasRows { get; }
    public bool NoRows { get; }
}
