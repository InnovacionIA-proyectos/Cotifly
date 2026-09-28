namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Base URL de Nexo (sección <c>Nexo</c>, variable <c>Nexo__UrlBase</c>), consumida por la búsqueda de clientes
/// (HU-005.2, ADR-005). Sin autenticación propia: cada petición manda <c>X-Portal-Usuario-Id</c> (el <c>idUsuarioExterno</c>
/// del comercial), no un token — Nexo resuelve el rol contra el mismo almacén corporativo que ClientePro.
/// Sin <see cref="UrlBase"/> (vacía, valor por defecto), <c>clsFabricaBuscadorClientes</c> usa
/// <see cref="Clientes.Services.clsBuscadorClientesNoDisponible"/>: no hay <c>[Required]</c> porque Nexo aún no es
/// obligatorio en todos los ambientes.
/// </summary>
public sealed class clsOpcionesNexo
{
    public const string Seccion = "Nexo";

    /// <summary>URL base de la API de Nexo (sin barra final), ej. <c>https://nexo.boterosoto.com.co/api</c>.</summary>
    public string? UrlBase { get; set; }
}
