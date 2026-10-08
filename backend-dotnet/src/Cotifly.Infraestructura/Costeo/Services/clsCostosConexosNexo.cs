using System.Text.Json;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Costo unitario de los conexos del catálogo de Nexo para la captura (HU-021.6, decisión 3): <c>GET /api/servicios-conexos</c> de
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

                encontrados[id] = new clsCostoConexoNexo(id, clsCosteoNexo.Texto(fila, "tipoServicio"), costo);
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

/// <summary>Costos de conexos cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): siempre <c>NEXO_NO_DISPONIBLE</c>.</summary>
public sealed class clsCostosConexosNoDisponible : IClsCostosConexosNexo
{
    public Task<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>> CostosAsync(
        Guid usuarioId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>>(
            new clsErrorDominio(clsCodigosError.NexoNoDisponible, clsServiciosConexosNoDisponible.Mensaje));
}
