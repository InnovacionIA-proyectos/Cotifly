using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;

namespace Cotifly.Aplicacion.Margen.Interfaces;

/// <summary>
/// Puerto (Strategy, ADR-002) de la rentabilidad por cliente de SAP TM (<c>FuenteMargen</c> del Python). HU-042.2 (#243, ADR-027): la
/// única implementación es <c>clsFuenteMargenTablaLocal</c>, que lee la copia local <c>dbo_cotifly.SapMargenCliente</c> que sincroniza a
/// diario el worker del backend (<see cref="IClsSincronizacionMargenSap"/>; el SQL Agent Job de HU-042.1 no se instala). Cotifly no llama a
/// SAP TM al cotizar: se retiraron los modos <c>export_json</c> y <c>odata</c>.
/// </summary>
public interface IClsFuenteMargen
{
    /// <summary>Etiqueta de la fuente (<c>tabla_local</c>...), para logs.</summary>
    string Modo { get; }

    /// <summary>
    /// Margen de SAP TM para (<paramref name="salesOrg"/>, <paramref name="trqType"/>, <paramref name="bpCliente"/>): con valor (porcentaje tal
    /// como viene de SAP), sin registro, o "no configurada" si la copia local nunca se sincronizó (el resolver usa la política de la línea
    /// en los dos últimos casos, con motivos distintos en el detalle).
    /// </summary>
    /// <exception cref="clsFuenteMargenNoDisponibleException">La fuente no se pudo leer (base caída, tiempo agotado): el resolver usa la política.</exception>
    Task<clsConsultaMargenSap> ConsultarAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-042.2 (#243): fin de la última corrida <c>ok</c> de la copia (<c>null</c> si nunca la hubo o si no se pudo leer: es para avisos, no
    /// lanza). Por defecto <c>null</c> (fuentes de prueba).
    /// </summary>
    Task<DateTimeOffset?> UltimaSincronizacionAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateTimeOffset?>(null);
}
