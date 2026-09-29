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
}

/// <summary>Implementación de <see cref="IClsPrecioCosteoService"/>.</summary>
public sealed class clsPrecioCosteoService(IClsPrecioService precios) : IClsPrecioCosteoService
{
    public const string MotivoSinMargen =
        "Nexo ya costeó la solicitud, pero la cotización no tiene margen resuelto: no se calcula el precio. Reenvíala o revisa la línea de negocio.";

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
