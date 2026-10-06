using Cotifly.Aplicacion.Redaccion.DTOs;

namespace Cotifly.Aplicacion.Redaccion.Interfaces;

/// <summary>
/// Puerto del redactor de la IA (HU-013.5, ADR-018 §2a): pide al modelo el asunto y la introducción del PDF en JSON con el esquema de
/// <see cref="Services.clsReglasTextosCotizacion"/>. Solo recibe <see cref="clsContextoRedaccion"/> (sin montos). No valida ni aplica
/// el respaldo: eso lo hace <see cref="Services.IClsRedaccionCotizacionService"/>. Puede lanzar (proveedor caído, cancelación).
/// </summary>
public interface IClsRedactorCotizacion
{
    /// <summary>Proveedor y modelo configurados (para la auditoría, también cuando la llamada falla).</summary>
    clsIdentidadRedactor Identidad { get; }

    /// <summary>Texto crudo del modelo (sin validar) y el modelo que respondió.</summary>
    Task<clsRespuestaRedactor> RedactarAsync(clsContextoRedaccion contexto, CancellationToken cancellationToken = default);
}
