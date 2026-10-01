using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Margen sobre el costeo de Nexo (HU-005.7, ADR-015 §5): cuando el costeo está completo (<c>autocosteo</c> o <c>entregada</c>) el
/// costo de la línea pasa por el módulo de Precio (HU-003.4) con el margen que se resolvió al enviar; el LLM nunca pone números
/// (ADR-003). El costo bruto de Nexo solo queda en <c>Cotizaciones.costo</c> (interno): el comercial ve el precio.
/// </summary>
public interface IClsPrecioCosteoService
{
    /// <summary>
    /// Calcula y guarda <c>precioFinal</c> (y el precio de cada servicio conexo) a partir de la foto de Nexo. Devuelve <c>true</c>
    /// si el precio cambió. Con el PDF ya generado no toca el precio: deja la advertencia en <c>motivoEstadoCosteo</c> si el
    /// costeo cambió.
    /// </summary>
    bool Aplicar(clsCotizacion cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora);

    /// <summary>
    /// Igual que <see cref="Aplicar(clsCotizacion, clsCosteoConsultadoNexo, DateTimeOffset)"/>, línea por línea (HU-011.2, ADR-017):
    /// asigna a cada línea su id de Nexo por orden, deriva su estado y su costo, y le pone precio con el margen de la cotización en
    /// cuanto tiene flete (aunque otras líneas sigan pendientes). La cotización suma los precios de sus líneas solo cuando el costeo
    /// está completo. Sin líneas (cotizaciones anteriores al 0005) se comporta como el método de una línea.
    /// </summary>
    bool Aplicar(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, clsCosteoConsultadoNexo foto, DateTimeOffset ahora);
}

/// <summary>Implementación de <see cref="IClsPrecioCosteoService"/>.</summary>
public sealed class clsPrecioCosteoService(IClsPrecioService precios) : IClsPrecioCosteoService
{
    public const string MotivoSinMargen =
        "Nexo ya costeó la solicitud, pero la cotización no tiene margen resuelto: no se calcula el precio. Reenvíala o revisa la línea de negocio.";

    public const string MotivoLineasDescuadradas =
        "Nexo devolvió un número de líneas distinto al de la cotización: no se pueden asociar las líneas para ponerles precio. Revísala con el equipo de Nexo.";

    public bool Aplicar(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas, clsCosteoConsultadoNexo foto, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        if (lineas is null || lineas.Count == 0)
        {
            return Aplicar(cotizacion, foto, ahora);
        }

        var ordenadas = lineas.OrderBy(l => l.Orden).ToList();

        // Ids de Nexo por orden: CapturaResultDto no devuelve los ids y GET /api/solicitudes/{id} los trae en el orden de captura
        // (pendiente de confirmar con Nexo, ADR-017). Sin el mismo número de líneas no se adivina.
        if (ordenadas.Any(l => l.LineaSolicitudNexoId is null))
        {
            if (foto.LineasSolicitud.Count == ordenadas.Count)
            {
                for (var i = 0; i < ordenadas.Count; i++)
                {
                    ordenadas[i].AsignarLineaNexo(foto.LineasSolicitud[i]);
                }
            }
            else if (foto.LineasSolicitud.Count > 0)
            {
                cotizacion.AdvertirCosteo(MotivoLineasDescuadradas);
                return false;
            }
        }

        // Con PDF el precio de la versión se conserva: las líneas solo actualizan su estado (ADR-015 §5).
        var conPdf = cotizacion.PdfPath is not null;
        var cambio = false;
        foreach (var linea in ordenadas)
        {
            var estadoLinea = clsDerivacionEstadoCosteo.DerivarLinea(foto, linea.LineaSolicitudNexoId);
            cambio |= conPdf
                ? linea.RegistrarEstado(estadoLinea)
                : linea.RegistrarCosteo(estadoLinea, clsDerivacionEstadoCosteo.CostoLinea(foto, linea.LineaSolicitudNexoId));
        }

        var margen = cotizacion.MargenAplicado;
        var metodo = cotizacion.MetodoCalculo;
        if (!conPdf && margen is { } m && metodo is not null)
        {
            foreach (var linea in ordenadas)
            {
                if (linea.Costo is not { } costoLinea)
                {
                    continue;
                }

                var precioLinea = precios.Calcular(costoLinea, m, metodo);
                if (precioLinea.EsFallo)
                {
                    cotizacion.AdvertirCosteo($"No se pudo calcular el precio de la línea {linea.Orden} sobre el costeo de Nexo: {precioLinea.Error.Mensaje}");
                    return false;
                }

                cambio |= linea.RegistrarPrecio(precioLinea.Valor.Precio);
            }
        }

        // Una sola línea: la cotización sigue exactamente la regla de HU-005.7 (mismos mensajes y el mismo "cambió").
        if (ordenadas.Count == 1)
        {
            return Aplicar(cotizacion, foto, ahora);
        }

        if (cotizacion.EstadoCosteo is not { } estado || !clsDerivacionEstadoCosteo.TieneCosteo(estado) || foto.Cotizacion is not { } nexo)
        {
            return cambio;
        }

        if (!conPdf && ordenadas.Count(l => l.Costo is null) is > 0 and var sinFlete)
        {
            cotizacion.AdvertirCosteo(
                $"Nexo entregó la cotización sin flete para {sinFlete} de {ordenadas.Count} líneas: no se calcula el precio total. Revísala con el equipo de Nexo.");
            return cambio;
        }

        var costo = ordenadas.Sum(l => clsDerivacionEstadoCosteo.CostoLinea(foto, l.LineaSolicitudNexoId) ?? 0m);
        if (margen is null || metodo is null)
        {
            cotizacion.AdvertirCosteo(MotivoSinMargen);
            return false;
        }

        if (conPdf)
        {
            if (cotizacion.Costo != costo)
            {
                cotizacion.AdvertirCosteo(
                    "El costeo de Nexo cambió después de generar el PDF: el precio de esta versión se conserva. Genera una versión nueva para cotizar con el costeo actual.");
            }

            return false;
        }

        var precio = ordenadas.Sum(l => l.PrecioFinal ?? 0m);
        var json = clsServiciosConexosJson.Escribir(ConexosConPrecio(cotizacion, nexo, margen.Value, metodo));
        if (cotizacion.Costo == costo && cotizacion.PrecioFinal == precio && cotizacion.ServiciosConexos == json)
        {
            return cambio;
        }

        cotizacion.RegistrarPrecioCosteo(costo, precio, json, ahora);
        return true;
    }

    /// <summary>Servicios conexos de Nexo con su precio (mismo margen y método) y la selección anterior del comercial (por defecto incluidos).</summary>
    private List<clsServicioConexoCosteo> ConexosConPrecio(clsCotizacion cotizacion, clsCotizacionNexo nexo, decimal margen, string metodo)
    {
        var anteriores = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var conexos = new List<clsServicioConexoCosteo>();
        foreach (var conexo in nexo.Conexos)
        {
            var precioConexo = precios.Calcular(conexo.ValorAplicado, margen, metodo);
            if (precioConexo.EsExitoso)
            {
                conexos.Add(new clsServicioConexoCosteo(
                    conexo.Id, conexo.Nombre, conexo.Categoria, conexo.Cantidad, conexo.ValorAplicado, precioConexo.Valor.Precio,
                    anteriores.TryGetValue(conexo.Id, out var anterior) ? anterior.Incluido : true));
            }
        }

        return conexos;
    }

    public bool Aplicar(clsCotizacion cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(foto);
        if (cotizacion.EstadoCosteo is not { } estado || !clsDerivacionEstadoCosteo.TieneCosteo(estado) || foto.Cotizacion is not { } nexo)
        {
            return false;
        }

        var lineas = foto.LineasSolicitud.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costo = nexo.Lineas.Where(l => l.LineaSolicitudId is { } id && lineas.Contains(id)).Sum(l => l.Costo);
        if (cotizacion.MargenAplicado is not { } margen || cotizacion.MetodoCalculo is not { } metodo)
        {
            cotizacion.AdvertirCosteo(MotivoSinMargen);
            return false;
        }

        var precio = precios.Calcular(costo, margen, metodo);
        if (precio.EsFallo)
        {
            cotizacion.AdvertirCosteo($"No se pudo calcular el precio sobre el costeo de Nexo: {precio.Error.Mensaje}");
            return false;
        }

        if (cotizacion.PdfPath is not null)
        {
            if (cotizacion.Costo != costo)
            {
                cotizacion.AdvertirCosteo(
                    "El costeo de Nexo cambió después de generar el PDF: el precio de esta versión se conserva. Genera una versión nueva para cotizar con el costeo actual.");
            }

            return false;
        }

        var anteriores = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos).ToDictionary(s => s.Id, StringComparer.Ordinal);
        var conexos = new List<clsServicioConexoCosteo>();
        foreach (var conexo in nexo.Conexos)
        {
            var precioConexo = precios.Calcular(conexo.ValorAplicado, margen, metodo);
            if (precioConexo.EsExitoso)
            {
                conexos.Add(new clsServicioConexoCosteo(
                    conexo.Id, conexo.Nombre, conexo.Categoria, conexo.Cantidad, conexo.ValorAplicado, precioConexo.Valor.Precio,
                    anteriores.TryGetValue(conexo.Id, out var anterior) ? anterior.Incluido : true));
            }
        }

        var json = clsServiciosConexosJson.Escribir(conexos);
        if (cotizacion.Costo == costo && cotizacion.PrecioFinal == precio.Valor.Precio && cotizacion.ServiciosConexos == json)
        {
            return false;
        }

        cotizacion.RegistrarPrecioCosteo(costo, precio.Valor.Precio, json, ahora);
        return true;
    }
}
