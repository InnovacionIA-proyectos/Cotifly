namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Opciones del envío de correo que consume el caso de uso (sección <c>Correo</c>; el transporte lee la misma sección en
/// <c>clsOpcionesCorreoTransporte</c> de Infraestructura). Port de <c>correo_habilitado</c> de <c>shared/config.py</c>.
/// </summary>
public sealed class clsOpcionesCorreo
{
    public const string Seccion = "Correo";

    /// <summary>
    /// Si es <c>false</c> (por defecto, igual que el Python: "funcionalidad diferida") ningún correo sale y el envío responde
    /// <c>CORREO_DESHABILITADO</c>; la vista previa y los destinatarios previos siguen funcionando.
    /// </summary>
    public bool Habilitado { get; set; }
}
