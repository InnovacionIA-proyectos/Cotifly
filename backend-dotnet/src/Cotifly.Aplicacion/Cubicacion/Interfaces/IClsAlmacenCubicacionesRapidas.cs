using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): dónde viven las cubicaciones rápidas que siguieron en segundo plano mientras el front las consulta.
/// Con una réplica de la API basta la memoria del proceso (<c>Cubicador:RetencionCubicacionMinutos</c>,
/// <c>Cubicador:MaximoCubicacionesEnMemoria</c>): si el proceso se reinicia, se pierden y el front recibe 404 (vuelve a cubicar). Con
/// varias réplicas haría falta una tabla (no existe todavía).
/// </summary>
public interface IClsAlmacenCubicacionesRapidas
{
    /// <summary>
    /// Registra una cubicación nueva <see cref="clsEstadosCubicacionRapida.EnProceso"/> de <paramref name="usuarioId"/>, o <c>null</c> si
    /// el almacén está lleno de cubicaciones en proceso (las terminadas más viejas se descartan primero).
    /// </summary>
    clsCubicacionRapida? Crear(Guid usuarioId);

    /// <summary>La cubicación <paramref name="id"/> de <paramref name="usuarioId"/>; <c>null</c> si no existe, venció o es de otro usuario.</summary>
    clsCubicacionRapida? Obtener(Guid id, Guid usuarioId);

    /// <summary>Terminó bien (renueva la retención). Si ya no existe, no hace nada.</summary>
    void Completar(Guid id, clsResultadoCubicacion resultado);

    /// <summary>Terminó con error (renueva la retención). Si ya no existe, no hace nada.</summary>
    void Fallar(Guid id, clsErrorDominio error);

    /// <summary>La borra (respondió dentro de la espera síncrona: nadie la va a consultar).</summary>
    void Descartar(Guid id);
}
