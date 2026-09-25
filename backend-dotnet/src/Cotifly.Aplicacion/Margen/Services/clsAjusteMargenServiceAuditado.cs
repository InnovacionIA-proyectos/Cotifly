using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Decorador de auditoría de <see cref="IClsAjusteMargenService"/> (#11): cada margen negociado queda en
/// <c>EventosHerramienta</c> como <c>registrar_ajuste_margen</c>, igual que lo auditaba el middleware del MCP en Python.
/// </summary>
public sealed class clsAjusteMargenServiceAuditado(IClsAjusteMargenService interno, IClsAuditoriaService auditoria) : IClsAjusteMargenService
{
    public Task<clsResultado<clsSalidaAjusteMargen>> RegistrarAsync(clsEntradaAjusteMargen entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return auditoria.AuditarAsync(
            clsAuditoriaService.HerramientaAjusteMargen,
            entrada,
            entrada.ThreadId,
            () => interno.RegistrarAsync(entrada, cancellationToken));
    }
}
