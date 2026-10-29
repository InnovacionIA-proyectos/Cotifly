using System.Globalization;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Port de <c>MargenResolver</c> (<c>mcp_server/margen/servicio.py</c>), en el mismo orden:
/// <list type="number">
/// <item>Línea → política (<c>MargenesPolitica</c>, por nombre) → TrqType con <c>SapMapeoTrqType</c> por el id de la política
/// (HU-042.4, #247; la fila de la organización pedida gana sobre el comodín <c>''</c>). Sin política no hay TrqType.</item>
/// <item>Si hay BP del cliente y TrqType: consulta la copia local de SAP TM (HU-042.2, #243, ADR-027) por (salesOrg, TrqType, BP).
/// Con valor → <c>sap_tm</c>, valor/100 a 6 decimales y anomalía fuera de [0, 60 %]; el detalle dice que viene de la tabla local, la
/// fecha de la fila y, si la última sincronización exitosa es vieja, un aviso (el dato se usa igual).</item>
/// <item>Sin BP no se consulta SAP (ADR-004, nunca con el NIT); sin BP, sin TrqType, sin registro (o fila inactiva) o con la
/// copia nunca sincronizada (no configurada) → política de la línea (<c>politica_linea</c>) con el motivo en <c>detalle</c>.</item>
/// <item>Si la fuente falla (base caída, tiempo agotado) → política de la línea con el motivo "SAP TM no disponible" y un warning en
/// el log (decisión de #13, Gherkin "Resiliencia si SAP no responde" de HU-009: la cotización no se interrumpe).</item>
/// <item>Sin política → <c>MARGEN_LINEA_DESCONOCIDA</c>. Antes de HU-042.4 un TrqType mapeado por texto permitía un margen de SAP sin
/// política; con la FK eso ya no es posible y el código <c>MARGEN_FUENTE_NO_DISPONIBLE</c> se retiró del contrato.</item>
/// </list>
/// A diferencia del Python, el nombre de la línea se compara sin tildes, mayúsculas ni espacios
/// contra las líneas de <c>MargenesPolitica</c> (la collation <c>CI_AS</c> distingue tildes, deuda de ADR-008).
/// </summary>
public sealed class clsMargenResolverService(
    IClsMargenPoliticaRepository politicas,
    IClsSapMapeoRepository mapeo,
    IClsFuenteMargen fuente,
    IOptions<clsOpcionesMargen> opciones,
    IClsReloj reloj,
    ILogger<clsMargenResolverService> logger) : IClsMargenResolverService
{
    /// <summary>Sin BP del cliente (prospecto o cliente no identificado): no se consulta SAP TM (ADR-004).</summary>
    public const string MotivoSinBp = "cliente nuevo: sin BP de SAP, no se consulta SAP TM";

    /// <summary>Con BP, pero sin TrqType mapeado o sin fila en SAP TM (texto del Python).</summary>
    public const string MotivoSinRegistro = "sin registro en SAP TM";

    /// <summary>La copia local de SAP TM nunca se sincronizó (ninguna corrida <c>ok</c> del job de Datos, HU-042.2): ambiente sin job.</summary>
    public const string MotivoNoConfigurado = "SAP TM no configurado: se usa la política de la línea";

    /// <summary>La fuente SAP TM no se pudo leer (decisión de #13: respaldo en la política).</summary>
    public const string MotivoNoDisponible = "SAP TM no disponible: se usa la política de la línea";

    private readonly string _salesOrgDefault = opciones.Value.SalesOrgDefault.Trim();
    private readonly int _avisoSincronizacionHoras = opciones.Value.AvisoSincronizacionHoras;

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
        if (politica is { Activa: false })
        {
            // #41 (auditoría F4): el comercial no cotiza con una línea inactiva (la administración sí la ve y la reactiva).
            return LineaInactiva(politica.LineaNegocio);
        }

        var linea = politica?.LineaNegocio ?? lineaRecibida;
        var metodo = politica is null || string.IsNullOrWhiteSpace(politica.MetodoCalculo)
            ? clsReglaMargen.MetodoMargen
            : politica.MetodoCalculo.Trim();

        // HU-042.4 (#247): el TrqType se busca por el id de la política (FK de SapMapeoTrqType), no por el texto. Sin política no hay
        // TrqType, así que tampoco se consulta SAP TM.
        var trqType = politica is null ? null : await mapeo.TrqTypeDeAsync(politica.Id, salesOrg, cancellationToken);

        // 1) SAP TM (copia local, HU-042.2): solo con BP del cliente y TrqType mapeado.
        clsConsultaMargenSap? consulta = null;
        var caidaSap = false;
        if (politica is not null && bp is not null && trqType is not null)
        {
            try
            {
                consulta = await fuente.ConsultarAsync(salesOrg, trqType, bp, cancellationToken);
            }
            catch (clsFuenteMargenNoDisponibleException ex)
            {
                caidaSap = true;
                logger.LogWarning(
                    "SAP TM ({Modo}) no disponible para {TrqType} en {SalesOrg}: {Motivo} Se usa la política de la línea.",
                    fuente.Modo, trqType, salesOrg, ex.Message);
            }

            if (consulta is { Estado: EstadoConsultaMargenSap.ConValor, Porcentaje: { } porcentaje })
            {
                var margenSap = clsReglasMargen.NormalizarValorSap(porcentaje);
                var anomaloSap = clsReglasMargen.EsAnomalo(margenSap);
                var detalleSap = DetalleSap(consulta, bp, trqType, porcentaje);
                if (anomaloSap)
                {
                    detalleSap += " — ANÓMALO (fuera de [0, 60%]): validar antes de aplicar.";
                }

                return new clsMargenResuelto(
                    new clsSalidaMargen(margenSap, clsCodigoEnum.ACodigo(OrigenMargen.SapTm), trqType, anomaloSap, detalleSap),
                    linea,
                    politica.Id,
                    clsReglasMargen.Redondear(politica.Margen),
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
        var motivo = (bp, trqType, consulta?.Estado) switch
        {
            _ when caidaSap => MotivoNoDisponible,
            (null, _, _) => MotivoSinBp,
            (_, null, _) => MotivoSinRegistro,
            (_, _, EstadoConsultaMargenSap.NoConfigurada) => MotivoNoConfigurado,
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
    /// HU-042.2 (#243): detalle de un margen de SAP TM leído de la copia local, con la fecha en que cambió la fila (en la zona de la
    /// propia fila) y, si la última sincronización exitosa tiene más de <see cref="clsOpcionesMargen.AvisoSincronizacionHoras"/>, el aviso
    /// de que puede estar desactualizado (el dato se usa igual: la copia no caduca).
    /// </summary>
    private string DetalleSap(clsConsultaMargenSap consulta, string bp, string trqType, decimal porcentaje)
    {
        var fuenteTexto = consulta.ActualizadoAt is { } actualizado
            ? $"SAP TM (tabla local, actualizado {actualizado.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})"
            : "SAP TM (tabla local)";
        var detalle = $"Margen propuesto {fuenteTexto} para cliente {bp} en {trqType}: {clsReglasMargen.Texto(porcentaje)}%";
        if (consulta.UltimaSincronizacion is { } ultima && reloj.AhoraUtc - ultima > TimeSpan.FromHours(_avisoSincronizacionHoras))
        {
            detalle += string.Create(CultureInfo.InvariantCulture,
                $" — sincronización con más de {_avisoSincronizacionHoras} h (última: {ultima.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}): el dato puede estar desactualizado.");
        }

        return detalle;
    }

    /// <summary>Detalle de <c>MARGEN_LINEA_DESCONOCIDA</c> cuando la línea existe pero está inactiva (#41).</summary>
    public const string DetalleLineaInactiva = "línea inactiva";

    /// <summary>Error de una línea inactiva: <c>MARGEN_LINEA_DESCONOCIDA</c> con el detalle <see cref="DetalleLineaInactiva"/>.</summary>
    public static clsErrorDominio LineaInactiva(string linea) =>
        new(clsCodigosError.MargenLineaDesconocida, $"La línea '{linea}' está inactiva: no se puede cotizar con ella.", DetalleLineaInactiva);

    /// <summary>Línea de negocio de Nexo para licitaciones (catálogo <c>GET /api/catalogos/lineas-negocio</c>); no es una línea de Cotifly.</summary>
    public const string LineaNexoLicitaciones = "Licitaciones";

    /// <summary><c>detalle.motivo</c> de <c>MARGEN_LINEA_DESCONOCIDA</c> cuando llega "Licitaciones" (la línea de Nexo) en una licitación.</summary>
    public const string DetalleLineaDeNexo = "LINEA_DE_NEXO";

    /// <summary>
    /// <c>MARGEN_LINEA_DESCONOCIDA</c> con las líneas que sí se pueden usar (HU-013.15, #124) en <c>detalle.lineas_validas</c>. Con
    /// <paramref name="esLicitacion"/> y la línea "Licitaciones" del catálogo de Nexo, el mensaje explica que esa línea es de Nexo y
    /// no da margen (<c>detalle.motivo = LINEA_DE_NEXO</c>): una licitación se crea con la línea de Cotifly que da el margen (decisión
    /// del 2026-10-08, pregunta abierta en <c>docs/conexion_nexo/HU-013-preguntas-nexo.md</c>).
    /// </summary>
    public static clsErrorDominio LineaDesconocida(string? linea, IReadOnlyList<string> activas, bool esLicitacion = false)
    {
        ArgumentNullException.ThrowIfNull(activas);
        var recibida = (linea ?? string.Empty).Trim();
        var lista = activas.Count == 0 ? string.Empty : $" Usa una de estas: {string.Join(", ", activas)}.";
        var deNexo = esLicitacion && clsReglasMargen.MismaLinea(recibida, LineaNexoLicitaciones);
        var mensaje = deNexo
            ? $"'{LineaNexoLicitaciones}' es una línea de negocio del catálogo de Nexo, no de Cotifly: una licitación se crea con la línea de "
              + "negocio de Cotifly que le da el margen (la misma para todas sus filas)." + lista
            : $"La línea de negocio '{recibida}' no existe en Cotifly." + lista;
        return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, mensaje, deNexo ? DetalleLineaDeNexo : null)
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["lineas_validas"] = activas },
        };
    }

    public async Task<IReadOnlyList<string>> LineasActivasAsync(CancellationToken cancellationToken = default) =>
        [.. (await politicas.ListarAsync(cancellationToken)).Where(p => p.Activa).Select(p => p.LineaNegocio).Order(StringComparer.Ordinal)];

    public Task<clsMargenPolitica?> ResolverLineaAsync(string? lineaNegocio, CancellationToken cancellationToken = default)
    {
        var linea = lineaNegocio?.Trim() ?? string.Empty;
        return clsReglasMargen.NormalizarLinea(linea).Length == 0
            ? Task.FromResult<clsMargenPolitica?>(null)
            : BuscarPoliticaAsync(linea, cancellationToken);
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
