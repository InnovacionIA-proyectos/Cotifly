using System.Globalization;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Port de <c>MargenResolver</c> (<c>mcp_server/margen/servicio.py</c>), en el mismo orden:
/// <list type="number">
/// <item>Línea → TrqType con <c>SapMapeoTrqType</c> (la fila de la organización pedida gana sobre el comodín <c>''</c>).</item>
/// <item>Si hay BP del cliente y TrqType: consulta la fuente SAP TM por (salesOrg, TrqType, BP). Con valor →
/// <c>sap_tm</c>, valor/100 a 6 decimales y anomalía fuera de [0, 60 %].</item>
/// <item>Sin BP no se consulta SAP (ADR-004, nunca con el NIT); sin BP, sin TrqType, sin registro en SAP o con la
/// fuente no configurada (hasta #13) → política de la línea (<c>politica_linea</c>) con el motivo en <c>detalle</c>.</item>
/// <item>Sin política → <c>MARGEN_LINEA_DESCONOCIDA</c>. Si una fuente configurada falla → <c>MARGEN_FUENTE_NO_DISPONIBLE</c>
/// (no cae a la política, igual que el Python).</item>
/// </list>
/// A diferencia del Python, el nombre de la línea se compara sin tildes, mayúsculas ni espacios
/// contra las líneas de <c>MargenesPolitica</c> (la collation <c>CI_AS</c> distingue tildes, deuda de ADR-008).
/// </summary>
public sealed class clsMargenResolverService(
    IClsMargenPoliticaRepository politicas,
    IClsSapMapeoRepository mapeo,
    IClsFuenteMargen fuente,
    IOptions<clsOpcionesMargen> opciones) : IClsMargenResolverService
{
    /// <summary>Sin BP del cliente (prospecto o cliente no identificado): no se consulta SAP TM (ADR-004).</summary>
    public const string MotivoSinBp = "cliente nuevo: sin BP de SAP, no se consulta SAP TM";

    /// <summary>Con BP, pero sin TrqType mapeado o sin fila en SAP TM (texto del Python).</summary>
    public const string MotivoSinRegistro = "sin registro en SAP TM";

    /// <summary>La fuente SAP TM no está configurada (hasta #13).</summary>
    public const string MotivoNoConfigurado = "SAP TM no configurado: se usa la política de la línea";

    private readonly string _salesOrgDefault = opciones.Value.SalesOrgDefault.Trim();

    public async Task<clsResultado<clsMargenResuelto>> ResolverAsync(clsEntradaMargen entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);

        var lineaRecibida = entrada.LineaNegocio?.Trim() ?? string.Empty;
        if (clsReglasMargen.NormalizarLinea(lineaRecibida).Length == 0)
        {
            return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, "La línea de negocio es obligatoria.");
        }

        var salesOrg = string.IsNullOrWhiteSpace(entrada.SalesOrg) ? _salesOrgDefault : entrada.SalesOrg.Trim();
        var bp = string.IsNullOrWhiteSpace(entrada.BpCliente) ? null : entrada.BpCliente.Trim();

        var politica = await BuscarPoliticaAsync(lineaRecibida, cancellationToken);
        var linea = politica?.LineaNegocio ?? lineaRecibida;
        var metodo = politica is null || string.IsNullOrWhiteSpace(politica.MetodoCalculo)
            ? clsReglaMargen.MetodoMargen
            : politica.MetodoCalculo.Trim();
        var trqType = await mapeo.TrqTypeDeAsync(linea, salesOrg, cancellationToken);

        // 1) SAP TM: solo con BP del cliente, TrqType mapeado y fuente configurada.
        if (bp is not null && trqType is not null && fuente.EstaConfigurada)
        {
            decimal? valor;
            try
            {
                valor = await fuente.ObtenerValorAsync(salesOrg, trqType, bp, cancellationToken);
            }
            catch (clsFuenteMargenNoDisponibleException ex)
            {
                return new clsErrorDominio(clsCodigosError.MargenFuenteNoDisponible, ex.Message);
            }

            if (valor is { } porcentaje)
            {
                var margenSap = clsReglasMargen.NormalizarValorSap(porcentaje);
                var anomaloSap = clsReglasMargen.EsAnomalo(margenSap);
                var detalleSap = $"Margen propuesto SAP TM para cliente {bp} en {trqType}: {porcentaje.ToString("0.00", CultureInfo.InvariantCulture)}%";
                if (anomaloSap)
                {
                    detalleSap += " — ANÓMALO (fuera de [0, 60%]): validar antes de aplicar.";
                }

                return new clsMargenResuelto(
                    new clsSalidaMargen(margenSap, clsCodigoEnum.ACodigo(OrigenMargen.SapTm), trqType, anomaloSap, detalleSap),
                    linea,
                    politica?.Id,
                    politica is null ? null : clsReglasMargen.Redondear(politica.Margen),
                    metodo);
            }
        }

        // 2) Respaldo: política de la línea.
        if (politica is null)
        {
            return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, $"Sin mapeo ni política para la línea '{lineaRecibida}'.");
        }

        var margen = clsReglasMargen.Redondear(politica.Margen);
        // Textos del Python ("cliente nuevo", "sin registro en SAP TM"); el de "sin BP" agrega el motivo de ADR-004.
        var motivo = (bp, trqType, fuente.EstaConfigurada) switch
        {
            (null, _, _) => MotivoSinBp,
            (_, null, _) => MotivoSinRegistro,
            (_, _, false) => MotivoNoConfigurado,
            _ => MotivoSinRegistro,
        };

        return new clsMargenResuelto(
            new clsSalidaMargen(
                margen,
                clsCodigoEnum.ACodigo(OrigenMargen.PoliticaLinea),
                trqType,
                clsReglasMargen.EsAnomalo(margen),
                $"Política de línea {linea} ({motivo}): {clsReglasMargen.Texto(margen)}."),
            linea,
            politica.Id,
            margen,
            metodo);
    }

    /// <summary>
    /// Política de la línea comparando en memoria sin tildes ni mayúsculas (8 líneas canónicas). Si dos líneas
    /// normalizan igual, gana la que coincide exactamente (sin distinguir mayúsculas) y luego la primera por nombre.
    /// </summary>
    private async Task<clsMargenPolitica?> BuscarPoliticaAsync(string lineaRecibida, CancellationToken cancellationToken)
    {
        var todas = await politicas.ListarAsync(cancellationToken);
        return todas
            .Where(p => clsReglasMargen.MismaLinea(p.LineaNegocio, lineaRecibida))
            .OrderBy(p => string.Equals(p.LineaNegocio, lineaRecibida, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.LineaNegocio, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
