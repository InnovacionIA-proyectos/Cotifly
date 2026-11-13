using System.Text.Json;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Costo unitario de los conexos del catálogo de Nexo para la captura: <c>GET /api/servicios-conexos</c> de
/// los activos, página por página (Nexo no tiene <c>GET</c> por id), hasta encontrar todos los ids pedidos o terminar el catálogo
/// (máximo <see cref="MaximoPaginas"/> páginas de <see cref="TamanoPagina"/>). Reutiliza el envío de <see cref="clsCosteoNexo"/>
/// (cliente <c>NexoCosteo</c>, identidad del comercial, límite de respuesta) y la traducción de errores de
/// <see cref="clsServiciosConexosNexo.Traducir"/>. El costo es INTERNO: solo se guarda como precio del conexo en la ruta.
/// </summary>
public sealed class clsCostosConexosNexo(clsCosteoNexo costeo) : IClsCostosConexosNexo
{
    public const int TamanoPagina = 100;
    public const int MaximoPaginas = 20;

    public async Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> CostosAsync(
        Guid usuarioId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var pendientes = new HashSet<string>(ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
        var encontrados = new Dictionary<string, clsCostoConexoNexo>(StringComparer.OrdinalIgnoreCase);
        for (var pagina = 1; pagina <= MaximoPaginas && pendientes.Count > 0; pagina++)
        {
            var ruta = clsServiciosConexosNexo.RutaCatalogo(new clsFiltroCatalogoConexos(null, null, pagina, TamanoPagina));
            var respuesta = await costeo.EnviarAsync(usuarioId, HttpMethod.Get, ruta, null, cancellationToken, clsServiciosConexosNexo.LimiteRespuestaBytes);
            if (respuesta.EsFallo)
            {
                return clsServiciosConexosNexo.Traducir(respuesta.Error, agregando: false);
            }

            using var documento = respuesta.Valor;
            var raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return new clsErrorDominio(clsCodigosError.NexoNoDisponible, clsServiciosConexosNexo.MensajeNoDisponible, "respuesta del catálogo sin forma de página");
            }

            var leidos = 0;
            foreach (var fila in items.EnumerateArray())
            {
                leidos++;
                if (clsCosteoNexo.Texto(fila, "id") is not { } id || !pendientes.Contains(id)
                    || (fila.TryGetProperty("activo", out var activo) && activo.ValueKind == JsonValueKind.False)
                    || clsCosteoNexo.Decimal(fila, "costoUnitario") is not { } costo)
                {
                    continue;
                }

                // precioVenta puede venir null (sin precio de venta: se cotiza al costo) o faltar.
                // También el codigoServicio, que se guarda con el conexo para la política de seguros.
                encontrados[id] = new clsCostoConexoNexo(
                    id, clsCosteoNexo.Texto(fila, "tipoServicio"), costo, clsCosteoNexo.Texto(fila, "categoria"), clsCosteoNexo.Decimal(fila, "precioVenta"),
                    clsCosteoNexo.Texto(fila, "codigoServicio"));
                pendientes.Remove(id);
            }

            var total = clsCosteoNexo.Entero(raiz, "total");
            if (leidos < TamanoPagina || (total is { } t && pagina * TamanoPagina >= t))
            {
                break;
            }
        }

        return clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(encontrados);
    }
}

/// <summary>
/// Copia en memoria (singleton) de los costos de conexos leídos del catálogo de Nexo: cada servicio vale <see cref="Vigencia"/> (5 min).
/// Solo se guardan los servicios encontrados; uno que no estaba se vuelve a buscar en la siguiente captura.
/// </summary>
public sealed class clsAlmacenCostosConexos
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (clsCostoConexoNexo Costo, DateTimeOffset LeidoAt)> _costos =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Leer(string id, DateTimeOffset ahora, out clsCostoConexoNexo costo)
    {
        if (_costos.TryGetValue(id, out var guardado) && ahora - guardado.LeidoAt < Vigencia)
        {
            costo = guardado.Costo;
            return true;
        }

        costo = null!;
        return false;
    }

    public void Guardar(clsCostoConexoNexo costo, DateTimeOffset ahora) => _costos[costo.Id] = (costo, ahora);
}

/// <summary>
/// Caché de <see cref="IClsCostosConexosNexo"/>: la validación y la preparación de una misma captura, y las capturas seguidas,
/// no vuelven a recorrer el catálogo de Nexo durante 5 minutos. El precio que se guarda en la ruta es el costo de Nexo al momento de
/// capturar; el reintento y el polling no vuelven a cotizar los conexos.
/// </summary>
public sealed class clsCostosConexosEnCache(IClsCostosConexosNexo interno, clsAlmacenCostosConexos almacen, Aplicacion.Comun.Interfaces.IClsReloj reloj)
    : IClsCostosConexosNexo
{
    public async Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> CostosAsync(
        Guid usuarioId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var ahora = reloj.AhoraUtc;
        var resultado = new Dictionary<string, clsCostoConexoNexo>(StringComparer.OrdinalIgnoreCase);
        var faltantes = new List<string>();
        foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (almacen.Leer(id, ahora, out var costo))
            {
                resultado[costo.Id] = costo;
            }
            else
            {
                faltantes.Add(id);
            }
        }

        if (faltantes.Count > 0)
        {
            var leidos = await interno.CostosAsync(usuarioId, faltantes, cancellationToken);
            if (leidos.EsFallo)
            {
                return leidos.Error;
            }

            foreach (var costo in leidos.Valor.Values)
            {
                almacen.Guardar(costo, ahora);
                resultado[costo.Id] = costo;
            }
        }

        return clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(resultado);
    }
}

/// <summary>Costos de conexos cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): siempre <c>NEXO_NO_DISPONIBLE</c>.</summary>
public sealed class clsCostosConexosNoDisponible : IClsCostosConexosNexo
{
    public Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> CostosAsync(
        Guid usuarioId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>>(
            new clsErrorDominio(clsCodigosError.NexoNoDisponible, clsServiciosConexosNoDisponible.Mensaje));
}
