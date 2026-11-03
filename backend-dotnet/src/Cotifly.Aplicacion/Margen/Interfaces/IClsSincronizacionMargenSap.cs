using Cotifly.Aplicacion.Margen.DTOs;

namespace Cotifly.Aplicacion.Margen.Interfaces;

/// <summary>
/// Puerto de la sincronización del margen de SAP TM hacia la copia local (HU-042.2, #243, ADR-027): la hace el backend (no un SQL Agent
/// Job). Una corrida descarga <c>RentabClienteSet</c> completo, resuelve el NIT por BP en el DW, carga <c>#SapMargenStage</c> y llama a
/// <c>dbo_cotifly.sp_sincronizar_margen_sap</c>. La implementación vive en Infraestructura; la usan el worker y la administración.
/// </summary>
/// <remarks>
/// Si la base de Cotifly no responde (al leer el estado o la última corrida, o al registrar un fallo) lanza
/// <see cref="Exceptions.clsSincronizacionMargenSapNoDisponibleException"/>.
/// </remarks>
public interface IClsSincronizacionMargenSap
{
    /// <summary>Hay <c>SapTm:Url</c>, <c>SapTm:User</c> y <c>SapTm:Pass</c>. Sin ellas no se sincroniza y el margen sigue con la política.</summary>
    bool Configurada { get; }

    /// <summary>Estado de la copia: última corrida, última <c>ok</c>, filas activas y próxima corrida estimada.</summary>
    Task<clsEstadoSincronizacionMargenSap> EstadoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Un intento de sincronización. Sin <paramref name="forzada"/>, solo corre si la última corrida <c>ok</c> tiene más de
    /// <c>SapTm:SincronizacionMinutos</c> (o si nunca la hubo); con ella, corre ya. No lanza por un fallo de SAP, del DW o de la carga: lo
    /// registra como <c>error</c> en <c>SapMargenSyncLog</c> y lo devuelve.
    /// </summary>
    Task<clsResultadoSincronizacionMargenSap> SincronizarAsync(bool forzada, CancellationToken cancellationToken = default);
}
