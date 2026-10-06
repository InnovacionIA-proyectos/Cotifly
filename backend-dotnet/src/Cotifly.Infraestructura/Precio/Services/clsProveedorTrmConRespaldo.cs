using System.Globalization;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// Cadena de fuentes de la TRM con respaldo (HU-012.1, ADR-014 §1): consulta la <paramref name="principal"/> (DolarApi) y, solo si falla,
/// la de <paramref name="respaldo"/> (Superintendencia Financiera). Si la principal falla y el respaldo responde, deja un warning con la
/// fuente usada (<see cref="clsTrm.Fuente"/>) para auditoría. Si las dos fallan, <c>TRM_NO_DISPONIBLE</c> con los dos motivos en el
/// detalle. No lanza: los adaptadores tampoco lanzan por la fuente. La caché por día (<see cref="clsProveedorTrmEnCache"/>) va encima.
/// </summary>
public sealed class clsProveedorTrmConRespaldo(
    IClsProveedorTrm principal, IClsProveedorTrm respaldo, ILogger<clsProveedorTrmConRespaldo> logger) : IClsProveedorTrm
{
    public const string Mensaje = "No se pudo obtener la TRM del día (ni DolarApi ni la Superintendencia Financiera respondieron con una TRM válida): la cotización en dólares no está disponible en este momento. Intenta de nuevo en unos minutos o cotiza en pesos.";

    public async Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        var primera = await principal.ObtenerAsync(fecha, cancellationToken);
        if (primera.EsExitoso)
        {
            return primera;
        }

        var segunda = await respaldo.ObtenerAsync(fecha, cancellationToken);
        var dia = fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (segunda.EsExitoso)
        {
            logger.LogWarning(
                "TRM del {Fecha} tomada de la fuente de respaldo {Fuente}: la principal falló ({Motivo}).",
                dia, segunda.Valor.Fuente, primera.Error.Detalle ?? primera.Error.Codigo);
            return segunda;
        }

        logger.LogWarning(
            "Sin TRM del {Fecha}: fallaron la fuente principal ({MotivoPrincipal}) y la de respaldo ({MotivoRespaldo}).",
            dia, primera.Error.Detalle ?? primera.Error.Codigo, segunda.Error.Detalle ?? segunda.Error.Codigo);
        return new clsErrorDominio(
            clsCodigosError.TrmNoDisponible,
            Mensaje,
            $"principal: {primera.Error.Detalle ?? primera.Error.Codigo}; respaldo: {segunda.Error.Detalle ?? segunda.Error.Codigo}");
    }
}
