using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Administracion.Controllers;

/// <summary>
/// Sincronización del margen de SAP TM (ADR-027; <c>specs/openapi.yaml</c> tag <c>admin</c>). Solo gerencia
/// (<c>gerente_comercial</c> o <c>director</c>), como el resto de <c>/web/admin/*</c>: política <see cref="clsPoliticas.GerenteComercial"/> aquí
/// y revalidación del rol en el caso de uso.
/// </summary>
/// <remarks>
/// <c>POST .../sincronizar</c> es síncrono: espera la corrida (segundos con ~1500 filas; como mucho <c>SapTm:TimeoutSegundos</c> más la carga) y
/// responde 200 con su resultado, también si quedó en <c>error</c> u <c>omitido</c> (el intento terminó; el estado es el dato). La corrida
/// usa el token de apagado de la API, no el de la petición: si el cliente se desconecta, la corrida termina igual y queda en el log.
/// </remarks>
[Route("web/admin/sap-margen")]
[Authorize(Policy = clsPoliticas.GerenteComercial)]
public sealed class clsSapMargenController(
    IClsContextoUsuario usuario,
    IClsSapMargenAdminService sapMargen,
    IHostApplicationLifetime vida) : clsControladorWebBase(usuario)
{
    /// <summary>200 <c>EstadoSincronizacionMargenSap</c>; 403 si no es gerencia; 503 <c>SAP_MARGEN_NO_CONFIGURADO</c>.</summary>
    [HttpGet("estado")]
    [ProducesResponseType<clsEstadoSincronizacionMargenSap>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Estado(CancellationToken cancellationToken)
    {
        var resultado = await sapMargen.EstadoAsync(cancellationToken);
        return resultado.Coincidir<IActionResult>(e => Ok(e), Error);
    }

    /// <summary>
    /// 200 <c>ResultadoSincronizacionMargenSap</c> (<c>ok</c>, <c>error</c> u <c>omitido</c>); 403; 409 <c>SAP_MARGEN_SINCRONIZACION_EN_CURSO</c>;
    /// 503 <c>SAP_MARGEN_NO_CONFIGURADO</c>.
    /// </summary>
    [HttpPost("sincronizar")]
    [ProducesResponseType<clsResultadoSincronizacionMargenSap>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorWeb>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Sincronizar()
    {
        var resultado = await sapMargen.SincronizarAsync(vida.ApplicationStopping);
        return resultado.Coincidir<IActionResult>(r => Ok(r), Error);
    }
}
