using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Caso de uso <c>enviar_correo</c> / <c>POST /web/cotizaciones/{folio}/enviar</c>. Errores de negocio:
/// <c>CONFIRMACION_REQUERIDA</c>, <c>CORREO_DESHABILITADO</c>, <c>COTIZACION_NO_ENCONTRADA</c> (no existe o no es del
/// comercial: el endpoint responde 404), <c>CORREO_DUPLICADO</c>, <c>CORREO_PDF_NO_ENCONTRADO</c>,
/// <c>CORREO_ENVIO_FALLIDO</c>; destinatarios, asunto o cuerpo inválidos → <c>CORREO_INVALIDO</c> (el endpoint responde
/// 400 con <c>ErrorWeb</c>).
/// </summary>
public interface IClsCorreoService
{
    Task<clsResultado<clsSalidaEnvioCorreo>> EnviarAsync(clsEntradaEnviarCorreo entrada, CancellationToken cancellationToken = default);
}

/// <summary>Vista previa del correo antes de enviarlo (<c>POST /web/cotizaciones/{folio}/correo/vista-previa</c>).</summary>
public interface IClsVistaPreviaCorreoService
{
    /// <summary>
    /// Asunto, cuerpo y destinatarios prediligenciados sin enviar nada: lo que venga en el borrador manda; si no trae
    /// destinatarios, los del último envío al cliente. <c>COTIZACION_NO_ENCONTRADA</c> si no es del comercial.
    /// </summary>
    Task<clsResultado<clsVistaPreviaCorreo>> PrevisualizarAsync(Guid usuarioId, string folio, clsBorradorCorreo? borrador = null, CancellationToken cancellationToken = default);
}

/// <summary>Destinatarios usados en el último envío a un cliente (<c>GET /web/clientes/{nit}/destinatarios-previos</c>).</summary>
public interface IClsDestinatariosPreviosService
{
    /// <summary>Solo envíos exitosos de cotizaciones del comercial; listas vacías si nunca se le envió.</summary>
    Task<clsDestinatarios> ObtenerAsync(Guid usuarioId, string? nitCliente, CancellationToken cancellationToken = default);
}
