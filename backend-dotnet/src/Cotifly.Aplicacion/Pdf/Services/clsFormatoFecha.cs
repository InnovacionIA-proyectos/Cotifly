using System.Globalization;

namespace Cotifly.Aplicacion.Pdf.Services;

/// <summary>
/// Fecha larga del PDF (<c>fecha_larga</c> del Python): <c>Yumbo, 24 de septiembre de 2026</c>. Meses propios en minúscula,
/// sin ICU ni la cultura del proceso. La fecha de hoy la da <c>IClsCalendarioNegocio</c> en la zona de negocio.
/// </summary>
public static class clsFormatoFecha
{
    /// <summary>Ciudad por defecto de la carta (la del Python).</summary>
    public const string CiudadPorDefecto = "Yumbo";

    private static readonly string[] _meses =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio",
        "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    /// <summary>Nombre del mes (1 = enero).</summary>
    public static string Mes(int mes) =>
        mes is >= 1 and <= 12 ? _meses[mes - 1] : throw new ArgumentOutOfRangeException(nameof(mes), mes, "El mes va de 1 a 12.");

    /// <summary>Fecha larga de una fecha conocida.</summary>
    public static string FechaLarga(DateOnly fecha, string ciudad) =>
        string.Create(CultureInfo.InvariantCulture, $"{ciudad}, {fecha.Day} de {Mes(fecha.Month)} de {fecha.Year}");

    /// <summary>
    /// Fecha larga a partir del texto ISO del payload (<c>AAAA-MM-DD</c>, admite <c>2026-3-9</c>). Sin fecha usa
    /// <paramref name="hoy"/>; si el texto no se entiende devuelve <c>{ciudad}, {texto}</c>, igual que el Python.
    /// </summary>
    public static string FechaLarga(string? iso, string ciudad, DateOnly hoy)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return FechaLarga(hoy, ciudad);
        }

        var partes = iso.Trim().Split('-');
        if (partes.Length == 3
            && int.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var anio)
            && int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var mes)
            && int.TryParse(partes[2], NumberStyles.None, CultureInfo.InvariantCulture, out var dia)
            && mes is >= 1 and <= 12)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{ciudad}, {dia} de {Mes(mes)} de {anio}");
        }

        return $"{ciudad}, {iso.Trim()}";
    }
}
