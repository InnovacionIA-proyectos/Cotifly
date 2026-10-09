namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Un paso del envío diferido de los conexos de una ruta a la cotización de Nexo (decisión 10 del usuario, HU-021.9, #136). Se guarda en
/// <c>EventosHerramienta</c> (<see cref="Herramienta"/>, sin columna nueva en el esquema), atado a la SOLICITUD de Nexo
/// (<see cref="SolicitudNexoId"/>): una re-solicitud o una <c>-vN</c> es otra solicitud y sus conexos se vuelven a enviar una vez.
/// El estado vigente de un conexo es el del registro de su intento más alto; dentro del mismo intento, el resultado manda sobre
/// <see cref="EnCurso"/> (no depende de la hora de cada fila).
/// </summary>
/// <param name="Folio">Folio de la cotización de Cotifly.</param>
/// <param name="SolicitudNexoId">Solicitud de Nexo a cuya cotización se envían.</param>
/// <param name="LineaSolicitudId">Ruta de Cotifly.</param>
/// <param name="Intento">Número del intento en la ruta (1, 2...).</param>
/// <param name="Estado"><see cref="EnCurso"/>, <see cref="Enviado"/>, <see cref="Incierto"/> o <see cref="Fallido"/>.</param>
/// <param name="Conexos">Ids de los conexos de la ruta (<c>IdServicioConexo</c>) a los que aplica el registro.</param>
/// <param name="PreviosNexo">Ids de los conexos que la cotización de Nexo ya tenía antes del POST (como el registro de #135).</param>
public sealed record clsEnvioConexosNexo(
    string Folio, string SolicitudNexoId, Guid LineaSolicitudId, int Intento, string Estado, IReadOnlyList<string> Conexos,
    IReadOnlyList<string> PreviosNexo)
{
    /// <summary>Herramienta con que se guarda en <c>EventosHerramienta</c>.</summary>
    public const string Herramienta = "conexos_envio_nexo";

    /// <summary>
    /// Se va a hacer el POST: se guarda ANTES de llamar a Nexo, en su propia transacción. Si el proceso muere a mitad del POST, el
    /// siguiente ciclo lo encuentra sin resultado y lo trata como <see cref="Incierto"/> (nunca repite el POST a ciegas).
    /// </summary>
    public const string EnCurso = "en_curso";

    /// <summary>El conexo está en la cotización de Nexo (2xx de Nexo, o una lectura lo muestra, o ya estaba).</summary>
    public const string Enviado = "enviado";

    /// <summary>
    /// No se sabe si Nexo lo agregó (tiempo agotado, red o 5xx) y la relectura no lo muestra: no se vuelve a enviar (Nexo no tiene DELETE
    /// ni idempotencia en el POST de conexos). Si una lectura posterior lo muestra, pasa a <see cref="Enviado"/>.
    /// </summary>
    public const string Incierto = "incierto";

    /// <summary>Nexo no lo aplicó (rechazo o error antes de enviar): el siguiente ciclo lo vuelve a intentar.</summary>
    public const string Fallido = "fallido";
}
