using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Captura por rutas (HU-021.6), común al formulario (<c>POST /web/solicitudes-costeo</c>) y a la licitación (<c>POST /web/licitaciones</c>):
/// valida TODAS las rutas juntas y las arma para <see cref="clsCotizacionSolicitud.RegistrarCaptura"/>.
/// </summary>
public interface IClsCapturaRutasService
{
    /// <summary>
    /// Todas las validaciones que dependen de catálogos o que solo aplican a una captura nueva, acumuladas sobre
    /// <paramref name="erroresPrevios"/> (los de forma y rango que ya calculó el llamador): forma de los conexos, códigos de catálogo,
    /// municipios (origen, destino y ciudades del contenedor: id y nombre, nunca adivina) y <see cref="clsLineaSolicitudCosteo.ErroresCaptura"/>.
    /// Devuelve las rutas con los municipios resueltos, todos los errores (encabezado primero, luego por ruta) y, si un catálogo de Nexo
    /// no respondió, ese error.
    /// </summary>
    Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> rutas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Arma las rutas ya validadas: sus conexos con el costo unitario del catálogo de Nexo como precio (decisión 3) y sus camiones
    /// (<see cref="IClsPlanificadorCamiones"/>). Un conexo que no está en el catálogo es un error de su ruta
    /// (<c>CONEXO_NO_ENCONTRADO</c>); Nexo caído o el planificador sin respuesta son <see cref="clsRutasPreparadas.Error"/>.
    /// </summary>
    Task<clsRutasPreparadas> PrepararAsync(
        Guid usuarioId,
        IReadOnlyList<clsRutaPorPreparar> rutas,
        CancellationToken cancellationToken = default);
}

/// <summary>Una ruta validada para <see cref="IClsCapturaRutasService.PrepararAsync"/>: la entrada, su línea de negocio e índice de origen.</summary>
public sealed record clsRutaPorPreparar(clsLineaLicitacionEntrada Ruta, int LineaNegocioId, int? RutaOrigenIndice);

/// <summary>Resultado de <see cref="IClsCapturaRutasService.PrepararAsync"/>: las rutas listas, o errores por ruta, o un error general.</summary>
public sealed record clsRutasPreparadas(IReadOnlyList<clsRutaCapturada> Rutas, IReadOnlyList<clsErrorFilaLicitacion> Errores, clsErrorDominio? Error);

/// <summary>Implementación de <see cref="IClsCapturaRutasService"/>.</summary>
public sealed class clsCapturaRutasService(
    IClsValidadorFilasLicitacion filas,
    IClsCostosConexosNexo costosConexos,
    IClsPlanificadorCamiones planificador,
    ILogger<clsCapturaRutasService> logger) : IClsCapturaRutasService
{
    /// <summary>Máximo de servicios conexos por ruta.</summary>
    public const int MaximoConexosPorRuta = 20;

    /// <summary>Cantidad máxima de un conexo (Nexo la maneja con 2 decimales).</summary>
    public const decimal CantidadMaximaConexo = 99_999.99m;

    /// <summary>Campo del id de municipio → campo del nombre: si el nombre ya tiene un error, el id no se reporta otra vez.</summary>
    private static readonly IReadOnlyDictionary<string, string> _nombreDelId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["origen_municipio_id"] = "origen",
        ["destino_municipio_id"] = "destino",
        ["ciudad_devolucion_municipio_id"] = "ciudad_devolucion_contenedor",
        ["ciudad_levante_municipio_id"] = "ciudad_levante_contenedor",
    };

    public async Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> rutas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        ArgumentNullException.ThrowIfNull(erroresPrevios);
        var previos = new List<clsErrorFilaLicitacion>(erroresPrevios);
        for (var i = 0; i < rutas.Count; i++)
        {
            if (rutas[i] is { } ruta)
            {
                previos.AddRange(ErroresConexos(ruta.Conexos).Select(e => new clsErrorFilaLicitacion(i, ruta.Fila, e.Campo, e.Codigo, e.Mensaje)));
            }
        }

        var validadas = await filas.ValidarAsync(usuarioId, rutas, previos, cancellationToken);
        var errores = new List<clsErrorFilaLicitacion>(validadas.Errores);
        // Con un catálogo de Nexo caído (el de municipios, o el maestro que se consultó primero y oculta si el de municipios respondió) los
        // ids que faltan no se exigen aquí: la captura responde 503 y se validan al volver a enviarla. Con el catálogo de municipios
        // disponible, un nombre que no se pudo resolver ya trae su propio error.
        var sinMunicipios = validadas.NoDisponible is not null;
        for (var i = 0; i < validadas.Lineas.Count; i++)
        {
            var indice = i;
            var ruta = validadas.Lineas[i];
            if (ruta?.Linea is null)
            {
                continue;
            }

            var campos = errores.Where(e => e.Indice == indice).Select(e => e.Campo).ToHashSet(StringComparer.Ordinal);
            foreach (var error in ruta.Linea.ErroresCaptura())
            {
                // Sin catálogo de municipios los ids que faltan se exigen al volver a enviarla; un id cuyo nombre ya falló no se repite.
                var esMunicipio = _nombreDelId.TryGetValue(error.Campo, out var nombre);
                if (esMunicipio && (sinMunicipios || campos.Contains(nombre!) || campos.Contains(error.Campo)))
                {
                    continue;
                }

                errores.Add(new clsErrorFilaLicitacion(indice, ruta.Fila, error.Campo, error.Codigo, error.Mensaje));
            }
        }

        return validadas with { Errores = [.. errores.OrderBy(e => e.Indice ?? -1)] };
    }

    public async Task<clsRutasPreparadas> PrepararAsync(
        Guid usuarioId, IReadOnlyList<clsRutaPorPreparar> rutas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        var ids = rutas
            .SelectMany(r => r.Ruta.Conexos ?? [])
            .Select(c => c?.IdServicioConexo?.Trim())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyDictionary<string, clsCostoConexoNexo> costos = new Dictionary<string, clsCostoConexoNexo>();
        if (ids.Count > 0)
        {
            var leidos = await costosConexos.CostosAsync(usuarioId, ids, cancellationToken);
            if (leidos.EsFallo)
            {
                return new clsRutasPreparadas([], [], leidos.Error);
            }

            costos = new Dictionary<string, clsCostoConexoNexo>(leidos.Valor, StringComparer.OrdinalIgnoreCase);
        }

        var errores = new List<clsErrorFilaLicitacion>();
        var preparadas = new List<clsRutaCapturada>(rutas.Count);
        for (var i = 0; i < rutas.Count; i++)
        {
            var (entrada, lineaNegocioId, indiceOrigen) = rutas[i];
            var conexos = new List<clsCambioConexo>();
            var pedidos = entrada.Conexos ?? [];
            for (var c = 0; c < pedidos.Count; c++)
            {
                var id = pedidos[c]!.IdServicioConexo!.Trim();
                if (!costos.TryGetValue(id, out var costo) || costo.CostoUnitario < 0m)
                {
                    errores.Add(new clsErrorFilaLicitacion(i, entrada.Fila, $"servicios_conexos[{c}].id_servicio_conexo", clsCodigosLineaSolicitud.ConexoNoEncontrado,
                        $"El servicio conexo '{id}' no está activo en el catálogo de Nexo (GET /web/servicios-conexos)."));
                    continue;
                }

                conexos.Add(new clsCambioConexo(costo.Id, costo.Nombre, costo.CostoUnitario, pedidos[c]!.Cantidad!.Value));
            }

            var camiones = await planificador.PlanificarAsync(usuarioId, entrada.Linea, cancellationToken);
            if (camiones.EsFallo)
            {
                logger.LogWarning("No se pudieron planificar los camiones de la ruta {Ruta} ({Codigo}): no se guarda la captura.", i + 1, camiones.Error.Codigo);
                return new clsRutasPreparadas([], [], camiones.Error);
            }

            preparadas.Add(new clsRutaCapturada(entrada.Linea, lineaNegocioId, indiceOrigen, camiones.Valor, conexos));
        }

        return errores.Count > 0 ? new clsRutasPreparadas([], errores, null) : new clsRutasPreparadas(preparadas, [], null);
    }

    /// <summary>
    /// Forma de los conexos de una ruta: id obligatorio (máx. <see cref="clsServicioConexoRuta.LargoMaximoId"/>), cantidad &gt; 0 con 2
    /// decimales, sin repetir el mismo servicio y hasta <see cref="MaximoConexosPorRuta"/>.
    /// </summary>
    public static IReadOnlyList<clsErrorCampo> ErroresConexos(IReadOnlyList<clsConexoCapturaEntrada?>? conexos)
    {
        var errores = new List<clsErrorCampo>();
        if (conexos is null || conexos.Count == 0)
        {
            return errores;
        }

        if (conexos.Count > MaximoConexosPorRuta)
        {
            errores.Add(new clsErrorCampo("servicios_conexos", clsCodigosLineaSolicitud.DemasiadosConexos,
                $"Una ruta admite máximo {MaximoConexosPorRuta} servicios conexos."));
            return errores;
        }

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < conexos.Count; i++)
        {
            var conexo = conexos[i];
            var id = conexo?.IdServicioConexo?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.CampoRequerido,
                    $"El servicio conexo {i + 1} debe traer su id del catálogo de Nexo (id_servicio_conexo)."));
            }
            else if (id.Length > clsServicioConexoRuta.LargoMaximoId)
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.CampoDemasiadoLargo,
                    $"El id del servicio conexo {i + 1} supera {clsServicioConexoRuta.LargoMaximoId} caracteres."));
            }
            else if (!vistos.Add(id))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.ConexoDuplicado,
                    $"El servicio conexo '{id}' está repetido en la ruta: indica la cantidad en una sola fila."));
            }

            if (conexo?.Cantidad is not { } cantidad)
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].cantidad", clsCodigosLineaSolicitud.CampoRequerido,
                    $"El servicio conexo {i + 1} debe traer la cantidad."));
            }
            else if (cantidad <= 0m || cantidad > CantidadMaximaConexo || cantidad != Math.Round(cantidad, clsSolicitudCosteo.Decimales))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].cantidad", clsCodigosLineaSolicitud.ValorFueraDeRango,
                    $"La cantidad del servicio conexo {i + 1} debe ser mayor que 0 y hasta {CantidadMaximaConexo} (máximo {clsSolicitudCosteo.Decimales} decimales)."));
            }
        }

        return errores;
    }
}
