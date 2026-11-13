namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Cómo terminó <c>POST /api/solicitudes/{id}/rechazar</c> de Nexo (<see cref="Interfaces.IClsCosteoNexo.RechazarSolicitudAsync"/>).
/// Las tres dejan la solicitud anterior sin vida en Nexo, así que la edición puede capturar de nuevo. Códigos (respuesta y auditoría):
/// <c>descartada</c> (204), <c>ya_descartada</c> (409 <c>SOLICITUD_YA_DESCARTADA</c>) y <c>no_encontrada</c> (404 <c>SOLICITUD_NO_ENCONTRADA</c>).
/// </summary>
public enum ResultadoRechazoNexo
{
    Descartada,
    YaDescartada,
    NoEncontrada,
}
