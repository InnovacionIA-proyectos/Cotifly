using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Envío diferido de los conexos de las rutas a la cotización de Nexo (decisión 10 del usuario, HU-021.9, #136). Los conexos se capturan
/// con la ruta (#132) o se copian en una re-solicitud o una <c>-vN</c>, pero el <c>POST /api/cotizaciones/{id}/conexos</c> exige la
/// cotización de Nexo y la línea del camión, que solo existen cuando Nexo costea: el polling los envía la primera vez que ve esa línea.
/// </summary>
public interface IClsEnvioConexosNexoService
{
    /// <summary>
    /// Envía UNA sola vez, a la línea del primer camión vigente de cada ruta, los conexos de la ruta que todavía no están en la cotización
    /// de Nexo <paramref name="cotizacionNexo"/> (la que acaba de leer el polling). Nunca lanza por Nexo: un fallo queda en el log y se
    /// reintenta en el siguiente ciclo, salvo que el POST haya sido incierto. Devuelve cuántos conexos quedaron enviados en esta llamada.
    /// </summary>
    Task<int> EnviarPendientesAsync(clsCotizacionSolicitud cotizacion, clsCotizacionNexo? cotizacionNexo, Guid usuarioId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsEnvioConexosNexoService"/>. Reglas:
/// <list type="bullet">
/// <item><b>Una sola vez por solicitud de Nexo.</b> Cada paso queda en <c>EventosHerramienta</c> (<see cref="clsEnvioConexosNexo"/>)
/// atado al folio y a <c>solicitudNexoId</c>: una re-solicitud o una <c>-vN</c> es otra solicitud y otra cotización de Nexo, así que sus
/// conexos se vuelven a enviar una vez.</item>
/// <item><b>Sin duplicados.</b> Un conexo que la cotización de Nexo ya muestra (por <c>servicioConexoId</c> o por su id) no se envía y
/// queda como enviado. Antes del POST se guarda <see cref="clsEnvioConexosNexo.EnCurso"/> con los conexos que Nexo ya tenía (el mismo
/// criterio del registro de #135): si el proceso muere a mitad, el siguiente ciclo lo trata como incierto y no repite el POST. Un POST
/// incierto (sin respuesta) se relee: si Nexo lo muestra queda enviado; si no, queda <see cref="clsEnvioConexosNexo.Incierto"/> y no se
/// vuelve a enviar (Nexo no tiene DELETE ni idempotencia en ese POST) hasta que una lectura lo muestre.</item>
/// <item><b>Primer camión vigente.</b> Van pegados a la línea del primer camión no descartado de la ruta (decisión 3); mientras esa línea
/// no esté en la cotización de Nexo, se espera.</item>
/// <item><b>El precio no cambia.</b> En Cotifly el conexo sigue con el costo del catálogo de Nexo que se guardó en la ruta; la lectura no lo
/// importa otra vez (<c>clsLecturaCosteo.ConexosNuevos</c> lo reconoce por su <c>servicioConexoId</c>).</item>
/// <item>Solo ids del catálogo (UUID): un conexo migrado con otro id no se envía.</item>
/// </list>
/// El polling la llama con la cotización reclamada (<c>ReclamarConsultaCosteoAsync</c>): dos réplicas nunca envían a la vez.
/// </summary>
public sealed class clsEnvioConexosNexoService(
    IClsCotizacionRepository cotizaciones,
    IClsServiciosConexosNexo nexo,
    IClsUnitOfWork unidadTrabajo,
    ILogger<clsEnvioConexosNexoService> logger) : IClsEnvioConexosNexoService
{
    public async Task<int> EnviarPendientesAsync(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo? cotizacionNexo, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (cotizacionNexo is null || cotizacion.SolicitudNexoId is not { } solicitud || cotizacion.CotizacionNexoId is not { } cotizacionNexoId
            || cotizacion.Rutas.All(r => r.Conexos.Count == 0))
        {
            return 0;
        }

        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        var enviados = 0;
        foreach (var ruta in cotizacion.RutasOrdenadas.Where(r => r.Conexos.Count > 0))
        {
            enviados += await EnviarRutaAsync(cotizacion, ruta, cotizacionNexo, cotizacionNexoId, solicitud, usuarioId,
                [.. registros.Where(r => r.LineaSolicitudId == ruta.Id)]);
        }

        return enviados;
    }

    private async Task<int> EnviarRutaAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsCotizacionNexo cotizacionNexo, string cotizacionNexoId, string solicitud,
        Guid usuarioId, IReadOnlyList<clsEnvioConexosNexo> registros)
    {
        var intento = registros.Count == 0 ? 0 : registros.Max(r => r.Intento);
        var estados = Estados(registros);
        var conexos = ruta.Conexos.Where(c => Guid.TryParse(c.IdServicioConexo, out _)).ToList();

        // Los que Nexo ya muestra quedan enviados (también los inciertos que la lectura confirmó).
        var confirmados = conexos
            .Where(c => EstaEnNexo(cotizacion, ruta, cotizacionNexo, c.IdServicioConexo)
                        && estados.GetValueOrDefault(c.IdServicioConexo) != clsEnvioConexosNexo.Enviado)
            .Select(c => c.IdServicioConexo)
            .ToList();
        if (confirmados.Count > 0)
        {
            intento++;
            cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, confirmados, cotizacionNexo));
        }

        var bloqueados = conexos
            .Where(c => !confirmados.Contains(c.IdServicioConexo)
                        && estados.GetValueOrDefault(c.IdServicioConexo) is clsEnvioConexosNexo.Incierto or clsEnvioConexosNexo.EnCurso)
            .ToList();
        if (bloqueados.Count > 0)
        {
            logger.LogWarning(
                "Conexos de la ruta {Ruta} de {Folio} con un envío incierto a Nexo: no se reenvían ({Conexos}). Revísalos en la cotización de Nexo.",
                ruta.Orden, cotizacion.Folio, string.Join(", ", bloqueados.Select(c => c.IdServicioConexo)));
        }

        var pendientes = conexos
            .Where(c => !confirmados.Contains(c.IdServicioConexo) && !bloqueados.Contains(c)
                        && estados.GetValueOrDefault(c.IdServicioConexo) is null or clsEnvioConexosNexo.Fallido)
            .ToList();
        var lineaId = pendientes.Count == 0
            ? null
            : clsServiciosConexosCosteoService.LineaDeNexo(cotizacionNexo, clsServiciosConexosCosteoService.PrimerCamion(ruta));
        if (lineaId is null)
        {
            // Nada que enviar, o la línea del primer camión vigente todavía no está en la cotización de Nexo: se espera al siguiente ciclo.
            if (confirmados.Count > 0)
            {
                await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            }

            return 0;
        }

        // El intento queda guardado ANTES del POST (que no es idempotente): si el proceso muere a mitad, nadie lo repite a ciegas.
        intento++;
        var ids = pendientes.Select(c => c.IdServicioConexo).ToList();
        cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.EnCurso, ids, cotizacionNexo));
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);

        var agregado = await nexo.AgregarAsync(
            usuarioId, cotizacionNexoId, [.. pendientes.Select(c => new clsConexoParaNexo(lineaId, c.IdServicioConexo, c.Cantidad))], CancellationToken.None);
        if (agregado.EsFallo)
        {
            // Nexo no lo aplicó: se reintenta en el siguiente ciclo.
            cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Fallido, ids, cotizacionNexo));
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            logger.LogWarning("No se pudieron enviar a Nexo los conexos de la ruta {Ruta} de {Folio} ({Codigo}); se reintenta en el siguiente ciclo.",
                ruta.Orden, cotizacion.Folio, agregado.Error.Codigo);
            return 0;
        }

        var enviados = ids;
        if (!agregado.Valor.Seguro)
        {
            // Sin respuesta: se relee la cotización de Nexo; lo que muestra quedó enviado y lo demás queda incierto (no se reenvía).
            var releida = agregado.Valor.Cotizacion is { } devuelta
                ? clsResultado<clsCotizacionNexo>.Exito(devuelta)
                : await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, CancellationToken.None);
            enviados = releida.EsExitoso ? [.. ids.Where(id => EstaEnNexo(cotizacion, ruta, releida.Valor, id))] : [];
            var inciertos = ids.Except(enviados, StringComparer.OrdinalIgnoreCase).ToList();
            if (inciertos.Count > 0)
            {
                cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Incierto, inciertos, cotizacionNexo));
                logger.LogWarning(
                    "Envío incierto a Nexo de los conexos de la ruta {Ruta} de {Folio} ({Conexos}): no se reenvían hasta que la cotización de Nexo los muestre.",
                    ruta.Orden, cotizacion.Folio, string.Join(", ", inciertos));
            }
        }

        if (enviados.Count > 0)
        {
            cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, enviados, cotizacionNexo));
        }

        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        return enviados.Count;
    }

    /// <summary>
    /// Estado vigente de cada conexo: el de su intento más alto; dentro del mismo intento, un resultado (enviado, incierto o fallido) manda
    /// sobre <see cref="clsEnvioConexosNexo.EnCurso"/>. Un intento que se quedó en curso (el proceso murió a mitad del POST) es incierto.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Estados(IReadOnlyList<clsEnvioConexosNexo> registros)
    {
        ArgumentNullException.ThrowIfNull(registros);
        var estados = new Dictionary<string, (int Intento, string Estado)>(StringComparer.OrdinalIgnoreCase);
        foreach (var registro in registros)
        {
            foreach (var id in registro.Conexos)
            {
                if (!estados.TryGetValue(id, out var actual) || registro.Intento > actual.Intento
                    || (registro.Intento == actual.Intento && actual.Estado == clsEnvioConexosNexo.EnCurso))
                {
                    estados[id] = (registro.Intento, registro.Estado);
                }
            }
        }

        return estados.ToDictionary(e => e.Key, e => e.Value.Estado, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ¿La cotización de Nexo ya tiene el conexo <paramref name="idServicio"/> en esta ruta? Por su <c>servicioConexoId</c> (o por su id, si
    /// Cotifly lo guardó con el id de Nexo), en una línea de un camión de la ruta o en una línea que no se puede ubicar (por las dudas: no se
    /// arriesga un duplicado).
    /// </summary>
    public static bool EstaEnNexo(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsCotizacionNexo cotizacionNexo, string idServicio)
    {
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        ArgumentNullException.ThrowIfNull(ruta);
        return cotizacionNexo.Conexos.Any(c =>
        {
            if (!string.Equals(c.ServicioConexoId, idServicio, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.Id, idServicio, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suRuta = clsServiciosConexosCosteoService.RutaDelConexo(cotizacion, cotizacionNexo, c);
            return suRuta is null || suRuta.Id == ruta.Id;
        });
    }

    private static clsEnvioConexosNexo Registro(
        clsCotizacionSolicitud cotizacion, string solicitud, clsLineaSolicitud ruta, int intento, string estado, IReadOnlyList<string> conexos,
        clsCotizacionNexo cotizacionNexo) =>
        new(cotizacion.Folio, solicitud, ruta.Id, intento, estado, conexos, [.. cotizacionNexo.Conexos.Select(c => c.Id)]);
}
