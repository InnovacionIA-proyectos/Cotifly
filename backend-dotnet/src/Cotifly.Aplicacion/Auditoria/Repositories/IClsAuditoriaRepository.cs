using Cotifly.Dominio.Auditoria;

namespace Cotifly.Aplicacion.Auditoria.Repositories;

/// <summary>Puerto de la auditoría de herramientas del agente (<c>EventosHerramienta</c>).</summary>
public interface IClsAuditoriaRepository
{
    void Registrar(clsEventoHerramienta evento);

    /// <summary>Eventos de un hilo, del más antiguo al más reciente (soporte).</summary>
    Task<IReadOnlyList<clsEventoHerramienta>> ListarPorThreadAsync(string threadId, CancellationToken cancellationToken = default);
}
