using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Folio de una cotización, generado siempre por el servidor (ADR-011, aprobado el 2026-09-25):
/// <c>PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]</c>.
/// <list type="bullet">
/// <item><c>DDMMAAAA</c>: fecha de negocio en <c>America/Bogota</c> (<c>IClsCalendarioNegocio</c>), nunca la UTC.</item>
/// <item><c>{nitCliente}</c>: solo si la cotización tiene NIT; se guardan únicamente sus dígitos (máximo 20).</item>
/// <item><c>{NNN}</c>: consecutivo diario del comercial, de <c>001</c> a <c>999</c>.</item>
/// <item><c>-vN</c>: solo en las versiones nuevas (N ≥ 2), sobre el folio base de la v1.</item>
/// </list>
/// Los folios anteriores al formato (<c>PCN-DDMMAAAA</c> del Python) no se interpretan, pero sí admiten versiones: su base es
/// el folio sin el sufijo <c>-vN</c>.
/// </summary>
public sealed partial record clsFolio
{
    public const string Prefijo = "PCN";
    public const int ConsecutivoMaximo = 999;
    public const int LargoMaximoNit = 20;
    public const string FormatoFecha = "ddMMyyyy";
    public const string CodigoInvalido = "FOLIO_INVALIDO";

    private clsFolio(string folioBase, int version, DateOnly? fecha, int? consecutivo, string? nitCliente)
    {
        Base = folioBase;
        Version = version;
        Fecha = fecha;
        Consecutivo = consecutivo;
        NitCliente = nitCliente;
    }

    /// <summary>Folio base (el de la v1), sin sufijo de versión.</summary>
    public string Base { get; }

    /// <summary>Versión (1 para la original). Es el valor de <c>Cotizaciones.version</c>.</summary>
    public int Version { get; }

    /// <summary>Fecha de negocio del folio, o <c>null</c> si es un folio anterior al formato.</summary>
    public DateOnly? Fecha { get; }

    /// <summary>Consecutivo diario del comercial, o <c>null</c> si es un folio anterior al formato.</summary>
    public int? Consecutivo { get; }

    /// <summary>Segmento del NIT (solo dígitos), o <c>null</c> si el folio no lo lleva.</summary>
    public string? NitCliente { get; }

    /// <summary>Folio completo: la base y, desde la v2, <c>-vN</c>.</summary>
    public string Valor => Version == 1 ? Base : $"{Base}-v{Version.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Folio de una cotización nueva (versión 1). <see cref="clsCodigosError.FolioConsecutivoAgotado"/> si el consecutivo no
    /// está entre 1 y 999; <see cref="CodigoInvalido"/> si el NIT tiene más de 20 dígitos.
    /// </summary>
    public static clsResultado<clsFolio> Crear(DateOnly fecha, int consecutivo, string? nitCliente)
    {
        if (consecutivo is < 1 or > ConsecutivoMaximo)
        {
            return new clsErrorDominio(
                clsCodigosError.FolioConsecutivoAgotado,
                $"El comercial ya usó los {ConsecutivoMaximo} folios del día {fecha.ToString(FormatoFecha, CultureInfo.InvariantCulture)}.");
        }

        var nit = SegmentoNit(nitCliente);
        if (nit is { Length: > LargoMaximoNit })
        {
            return new clsErrorDominio(CodigoInvalido, $"El NIT del cliente no puede tener más de {LargoMaximoNit} dígitos.");
        }

        var texto = new StringBuilder(Prefijo).Append('-');
        if (nit is not null)
        {
            texto.Append(nit).Append('-');
        }

        texto.Append(fecha.ToString(FormatoFecha, CultureInfo.InvariantCulture))
            .Append('-')
            .Append(consecutivo.ToString("000", CultureInfo.InvariantCulture));
        return new clsFolio(texto.ToString(), 1, fecha, consecutivo, nit);
    }

    /// <summary>
    /// Interpreta un folio existente (del formato actual o anterior). Un folio vacío, de más de 50 caracteres o con caracteres
    /// fuera de <c>[A-Za-z0-9_-]</c> es <see cref="CodigoInvalido"/>.
    /// </summary>
    public static clsResultado<clsFolio> Desde(string? folio)
    {
        var valor = folio?.Trim();
        if (string.IsNullOrEmpty(valor) || valor.Length > clsCotizacion.LargoMaximoFolio || !ExpresionCaracteres().IsMatch(valor))
        {
            return new clsErrorDominio(CodigoInvalido, "El folio no es válido.");
        }

        var version = 1;
        var folioBase = valor;
        var sufijo = ExpresionVersion().Match(valor);
        if (sufijo.Success
            && int.TryParse(sufijo.Groups["version"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n >= 2)
        {
            version = n;
            folioBase = valor[..sufijo.Index];
        }

        var partes = ExpresionFormato().Match(folioBase);
        if (!partes.Success
            || !DateOnly.TryParseExact(partes.Groups["fecha"].Value, FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
        {
            return new clsFolio(folioBase, version, null, null, null);
        }

        var nit = partes.Groups["nit"].Success ? partes.Groups["nit"].Value : null;
        var consecutivo = int.Parse(partes.Groups["nnn"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return new clsFolio(folioBase, version, fecha, consecutivo, nit);
    }

    /// <summary>
    /// Folio de la versión <paramref name="version"/> (≥ 2) sobre esta base. <see cref="CodigoInvalido"/> si el resultado
    /// supera los 50 caracteres de la columna.
    /// </summary>
    public clsResultado<clsFolio> ConVersion(int version)
    {
        if (version < 2)
        {
            return new clsErrorDominio(CodigoInvalido, "Una versión nueva debe ser la 2 o mayor.");
        }

        var nuevo = new clsFolio(Base, version, Fecha, Consecutivo, NitCliente);
        return nuevo.Valor.Length > clsCotizacion.LargoMaximoFolio
            ? new clsErrorDominio(CodigoInvalido, $"El folio de la versión {version} supera {clsCotizacion.LargoMaximoFolio} caracteres.")
            : nuevo;
    }

    /// <summary>
    /// Segmento <c>DDMMAAAA</c> de una fecha, el que comparten todos los folios del día (para contar el consecutivo).
    /// </summary>
    public static string SegmentoFecha(DateOnly fecha) => fecha.ToString(FormatoFecha, CultureInfo.InvariantCulture);

    /// <summary>Solo los dígitos del NIT (<c>900.123.456-7</c> → <c>9001234567</c>), o <c>null</c> si no trae ninguno.</summary>
    public static string? SegmentoNit(string? nitCliente)
    {
        if (string.IsNullOrWhiteSpace(nitCliente))
        {
            return null;
        }

        var digitos = new string(nitCliente.Where(char.IsAsciiDigit).ToArray());
        return digitos.Length == 0 ? null : digitos;
    }

    public override string ToString() => Valor;

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionCaracteres();

    [GeneratedRegex(@"-v(?<version>[0-9]{1,6})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionVersion();

    [GeneratedRegex(@"^PCN-(?:(?<nit>[0-9]{1,20})-)?(?<fecha>[0-9]{8})-(?<nnn>[0-9]{3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionFormato();
}
