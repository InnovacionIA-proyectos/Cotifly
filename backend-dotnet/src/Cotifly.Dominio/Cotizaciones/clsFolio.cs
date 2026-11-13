using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Folio de una cotización, generado siempre por el servidor (ADR-011):
/// <c>PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]</c>, p. ej. <c>PCN-DG-25092026-001</c> o
/// <c>PCN-9000472281-DG-25092026-001-v2</c>.
/// <list type="bullet">
/// <item><c>{nitCliente}</c>: solo si la cotización tiene NIT; se guardan únicamente sus dígitos (máximo
/// <see cref="LargoMaximoNit"/>).</item>
/// <item><c>{INICIALES}</c>: dos letras A–Z del comercial (<see cref="clsInicialesComercial"/>).</item>
/// <item><c>DDMMAAAA</c>: fecha de negocio en <c>America/Bogota</c> (<c>IClsCalendarioNegocio</c>), nunca la UTC.</item>
/// <item><c>{NNN}</c>: consecutivo diario de esas iniciales, de <c>001</c> a <c>999</c>.</item>
/// <item><c>-vN</c>: solo en las versiones nuevas (N ≥ 2), sobre el folio base de la v1.</item>
/// </list>
/// Los folios anteriores al formato (<c>PCN-DDMMAAAA</c>, o <c>PCN-[nit-]DDMMAAAA-NNN</c> sin iniciales) no se
/// interpretan, pero sí admiten versiones: su base es el folio sin el sufijo <c>-vN</c>.
/// </summary>
public sealed partial record clsFolio
{
    public const string Prefijo = "PCN";
    public const int ConsecutivoMaximo = 999;
    public const string FormatoFecha = "ddMMyyyy";
    public const string CodigoInvalido = "FOLIO_INVALIDO";

    /// <summary>
    /// Largo de todo lo que no es NIT en el folio más largo que se admite: <c>PCN-</c> + <c>-</c> + iniciales + <c>-</c> +
    /// <c>DDMMAAAA</c> + <c>-NNN</c> + <c>-v99</c> = 24 caracteres.
    /// </summary>
    public const int LargoSinNit = 4 + 1 + clsInicialesComercial.Largo + 1 + 8 + 4 + 4;

    /// <summary>
    /// Máximo de dígitos del NIT: el que deja caber hasta la versión 99 en los 50 caracteres de <c>Cotizaciones.folio</c>
    /// (50 − 24 = 26; un NIT colombiano tiene 9 o 10).
    /// </summary>
    public const int LargoMaximoNit = clsCotizacionSolicitud.LargoMaximoFolio - LargoSinNit;

    private clsFolio(string folioBase, int version, DateOnly? fecha, int? consecutivo, string? nitCliente, string? iniciales)
    {
        Base = folioBase;
        Version = version;
        Fecha = fecha;
        Consecutivo = consecutivo;
        NitCliente = nitCliente;
        Iniciales = iniciales;
    }

    /// <summary>Folio base (el de la v1), sin sufijo de versión.</summary>
    public string Base { get; }

    /// <summary>Versión (1 para la original). Es el valor de <c>Cotizaciones.version</c>.</summary>
    public int Version { get; }

    /// <summary>Fecha de negocio del folio, o <c>null</c> si es un folio anterior al formato.</summary>
    public DateOnly? Fecha { get; }

    /// <summary>Consecutivo diario de las iniciales, o <c>null</c> si es un folio anterior al formato.</summary>
    public int? Consecutivo { get; }

    /// <summary>Segmento del NIT (solo dígitos), o <c>null</c> si el folio no lo lleva.</summary>
    public string? NitCliente { get; }

    /// <summary>Iniciales del comercial, o <c>null</c> si es un folio anterior al formato.</summary>
    public string? Iniciales { get; }

    /// <summary>Folio completo: la base y, desde la v2, <c>-vN</c>.</summary>
    public string Valor => Version == 1 ? Base : $"{Base}-v{Version.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Folio de una cotización nueva (versión 1). <see cref="clsCodigosError.FolioConsecutivoAgotado"/> si el consecutivo no
    /// está entre 1 y 999; <see cref="CodigoInvalido"/> si las iniciales no son dos letras A–Z en mayúscula o si el NIT tiene
    /// más de <see cref="LargoMaximoNit"/> dígitos.
    /// </summary>
    public static clsResultado<clsFolio> Crear(DateOnly fecha, string iniciales, int consecutivo, string? nitCliente)
    {
        if (!clsInicialesComercial.EsValida(iniciales))
        {
            return new clsErrorDominio(CodigoInvalido, "Las iniciales del comercial deben ser dos letras A-Z en mayúscula.");
        }

        if (consecutivo is < 1 or > ConsecutivoMaximo)
        {
            return new clsErrorDominio(
                clsCodigosError.FolioConsecutivoAgotado,
                $"Ya se usaron los {ConsecutivoMaximo} folios de las iniciales {iniciales} del día {SegmentoFecha(fecha)}.");
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

        texto.Append(iniciales)
            .Append('-')
            .Append(SegmentoFecha(fecha))
            .Append('-')
            .Append(consecutivo.ToString("000", CultureInfo.InvariantCulture));
        return new clsFolio(texto.ToString(), 1, fecha, consecutivo, nit, iniciales);
    }

    /// <summary>
    /// Interpreta un folio existente (del formato actual o anterior). Un folio vacío, de más de 50 caracteres o con caracteres
    /// fuera de <c>[A-Za-z0-9_-]</c> es <see cref="CodigoInvalido"/>.
    /// </summary>
    public static clsResultado<clsFolio> Desde(string? folio)
    {
        var valor = folio?.Trim();
        if (string.IsNullOrEmpty(valor) || valor.Length > clsCotizacionSolicitud.LargoMaximoFolio || !ExpresionCaracteres().IsMatch(valor))
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
            return new clsFolio(folioBase, version, null, null, null, null);
        }

        var nit = partes.Groups["nit"].Success ? partes.Groups["nit"].Value : null;
        var consecutivo = int.Parse(partes.Groups["nnn"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return new clsFolio(folioBase, version, fecha, consecutivo, nit, partes.Groups["ini"].Value);
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

        var nuevo = new clsFolio(Base, version, Fecha, Consecutivo, NitCliente, Iniciales);
        return nuevo.Valor.Length > clsCotizacionSolicitud.LargoMaximoFolio
            ? new clsErrorDominio(CodigoInvalido, $"El folio de la versión {version} supera {clsCotizacionSolicitud.LargoMaximoFolio} caracteres.")
            : nuevo;
    }

    /// <summary>Segmento <c>DDMMAAAA</c> de una fecha, el que comparten todos los folios del día (para contar el consecutivo).</summary>
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

    [GeneratedRegex(@"^PCN-(?:(?<nit>[0-9]{1,26})-)?(?<ini>[A-Z]{2})-(?<fecha>[0-9]{8})-(?<nnn>[0-9]{3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionFormato();
}
