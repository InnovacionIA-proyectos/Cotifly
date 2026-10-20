using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Reglas de captura de las filas de una licitación que dependen de catálogos (HU-013.11, #108): códigos de catálogo (G2, con
/// <see cref="IClsValidadorCatalogosCaptura"/>, la misma regla del formulario) e ids de municipio (G6, <see cref="clsMunicipiosLicitacion"/>).
/// Las usan la creación (<c>POST /web/licitaciones</c>) y todo reenvío de lo guardado: el reintento y la re-solicitud (mismo folio o
/// <c>-vN</c>), para que una licitación guardada antes de una regla nueva nunca llegue a Nexo con texto libre.
/// </summary>
public interface IClsValidadorFilasLicitacion
{
    /// <summary>
    /// Valida las filas (catálogos y municipios) y devuelve las líneas con los ids resueltos, todos los errores (los de
    /// <paramref name="erroresPrevios"/> más los nuevos, ordenados: encabezado primero y luego por línea) y, si un catálogo no está
    /// disponible, ese error. Con el catálogo maestro caído los errores traen igual lo que se valida sin Nexo.
    /// </summary>
    Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Vuelve a validar las líneas guardadas de una licitación antes de reenviarlas a Nexo: forma y rangos, G3, códigos de catálogo y
    /// municipios (las filas sin id se resuelven; las que ya lo tienen se respetan). Si alguna fila ya no pasa, <c>LICITACION_INVALIDA</c>
    /// con <c>detalle.motivo = LICITACION_REQUIERE_NUEVA_CARGA</c> y <c>detalle.errores</c> (las mismas filas de
    /// <c>ReporteLicitacionInvalida</c>): ni el reintento ni una <c>-vN</c> la arreglan, hay que cargarla de nuevo. Si un catálogo no
    /// está disponible, ese error (503). Si todo pasa, las líneas con los ids de municipio resueltos, en el mismo orden.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsLineaSolicitudCosteo>>> RevalidarGuardadasAsync(
        Guid usuarioId,
        string folio,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> lineas,
        IReadOnlyList<int?> filas,
        CancellationToken cancellationToken = default);
}

/// <summary>Resultado de <see cref="IClsValidadorFilasLicitacion.ValidarAsync"/>.</summary>
public sealed record clsFilasLicitacionValidadas(
    IReadOnlyList<clsLineaLicitacionEntrada> Lineas, IReadOnlyList<clsErrorFilaLicitacion> Errores, clsErrorDominio? NoDisponible);

/// <summary>Implementación de <see cref="IClsValidadorFilasLicitacion"/>.</summary>
public sealed class clsValidadorFilasLicitacion(
    IClsValidadorCatalogosCaptura catalogos, IClsMunicipiosProvider municipios, ILogger<clsValidadorFilasLicitacion>? logger = null)
    : IClsValidadorFilasLicitacion
{
    /// <summary><c>detalle.motivo</c> de una licitación guardada que ya no cumple las reglas de captura: se debe cargar de nuevo.</summary>
    public const string MotivoRequiereNuevaCarga = "LICITACION_REQUIERE_NUEVA_CARGA";

    public Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default) =>
        ValidarAsync(usuarioId, lineas, erroresPrevios, captura: true, cancellationToken);

    /// <summary>
    /// <paramref name="captura"/>: captura nueva (HU-021.6, decisión 2): resuelve también las ciudades del contenedor, verifica cada id
    /// contra el catálogo y copia el nombre del catálogo (un nombre distinto del cliente se ignora y queda en el log). Al revalidar lo
    /// guardado solo se resuelven el origen y el destino sin id (las rutas anteriores a HU-021 solo guardaron el nombre de las ciudades).
    /// </summary>
    private async Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        bool captura,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(erroresPrevios);
        var errores = new List<clsErrorFilaLicitacion>(erroresPrevios);
        var codigos = await catalogos.ValidarLineasAsync(usuarioId, [.. lineas.Select(l => l?.Linea!)], cancellationToken);
        var noDisponible = codigos.NoDisponible;
        for (var i = 0; i < lineas.Count; i++)
        {
            var indice = i;
            errores.AddRange(codigos.Errores[i].Select(e => new clsErrorFilaLicitacion(indice, lineas[indice]?.Fila, e.Campo, e.Codigo, e.Mensaje)));
        }

        // D-1019B-2 (#188 M1): el vehículo digitado se guarda escrito como en el catálogo de Nexo (como viaja), no como lo digitó el comercial.
        IReadOnlyList<clsLineaLicitacionEntrada?> resultantes = codigos.Canonicas is { } canonicas && canonicas.Count == lineas.Count
            ? [.. lineas.Select((l, i) => l is null || ReferenceEquals(canonicas[i], l.Linea) || canonicas[i] is null ? l : l with { Linea = canonicas[i] })]
            : lineas;
        if (clsMunicipiosLicitacion.NecesitaCatalogo(resultantes, captura))
        {
            var catalogo = await municipios.ObtenerAsync(usuarioId, cancellationToken);
            if (catalogo.EsFallo)
            {
                noDisponible ??= catalogo.Error;
            }
            else
            {
                var avisos = new List<string>();
                var resueltas = clsMunicipiosLicitacion.Resolver(resultantes, catalogo.Valor, captura, avisos);
                foreach (var aviso in avisos)
                {
                    logger?.LogWarning("Municipio de la captura: {Aviso}", aviso);
                }

                resultantes = resueltas.Lineas;
                errores.AddRange(resueltas.Errores);
            }
        }

        return new clsFilasLicitacionValidadas([.. resultantes.Select(l => l!)], [.. errores.OrderBy(e => e.Indice ?? -1)], noDisponible);
    }

    public async Task<clsResultado<IReadOnlyList<clsLineaSolicitudCosteo>>> RevalidarGuardadasAsync(
        Guid usuarioId,
        string folio,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> lineas,
        IReadOnlyList<int?> filas,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(filas);
        var entradas = lineas.Select((l, i) => (clsLineaLicitacionEntrada?)new clsLineaLicitacionEntrada(i < filas.Count ? filas[i] : null, l)).ToList();
        var previos = encabezado.Errores().Select(e => new clsErrorFilaLicitacion(null, null, e.Campo, e.Codigo, e.Mensaje))
            .Concat(entradas.SelectMany((l, i) => l!.Linea.Errores().Select(e => new clsErrorFilaLicitacion(i, l.Fila, e.Campo, e.Codigo, e.Mensaje))))
            .ToList();

        var validadas = await ValidarAsync(usuarioId, entradas, previos, captura: false, cancellationToken);
        if (validadas.Errores.Count > 0)
        {
            return RequiereNuevaCarga(folio, validadas.Errores);
        }

        if (validadas.NoDisponible is { } noDisponible)
        {
            return noDisponible;
        }

        return clsResultado<IReadOnlyList<clsLineaSolicitudCosteo>>.Exito([.. validadas.Lineas.Select(l => l.Linea)]);
    }

    /// <summary><c>LICITACION_INVALIDA</c> con el motivo <see cref="MotivoRequiereNuevaCarga"/> y las filas que fallan en el detalle.</summary>
    public static clsErrorDominio RequiereNuevaCarga(string folio, IReadOnlyList<clsErrorFilaLicitacion> errores)
    {
        ArgumentNullException.ThrowIfNull(errores);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> filas = [.. errores.Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["indice"] = e.Indice,
            ["fila"] = e.Fila,
            ["fila_excel"] = e.FilaExcel,
            ["campo"] = e.Campo,
            ["codigo"] = e.Codigo,
            ["mensaje"] = e.Mensaje,
        })];
        return new clsErrorDominio(
            clsCodigosError.LicitacionInvalida,
            $"La licitación {folio} tiene {errores.Count} error(es) en sus filas guardadas: ya no cumple las reglas de captura hacia Nexo "
            + "(códigos de catálogo, contenedor y aprovisionamiento o municipios) y no se puede reenviar ni versionar. Crea una licitación "
            + "nueva con el pliego corregido (POST /web/licitaciones).",
            MotivoRequiereNuevaCarga)
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["errores"] = filas },
        };
    }
}
