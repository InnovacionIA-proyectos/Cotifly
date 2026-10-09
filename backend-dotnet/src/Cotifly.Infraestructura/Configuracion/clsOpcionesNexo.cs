using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Base URL de Nexo (sección <c>Nexo</c>, variable <c>Nexo__UrlBase</c>), consumida por la búsqueda de clientes
/// (HU-005.2, ADR-005), municipios, catálogos de flete, cubicación y el costeo (ADR-015). Sin autenticación propia: cada petición manda <c>X-Portal-Usuario-Id</c> (el <c>idUsuarioExterno</c>
/// del comercial), no un token — Nexo resuelve el rol contra el mismo almacén corporativo que ClientePro.
/// Sin <see cref="UrlBase"/> (vacía, valor por defecto), <c>clsFabricaBuscadorClientes</c> usa
/// <see cref="Clientes.Services.clsBuscadorClientesNoDisponible"/>: no hay <c>[Required]</c> porque Nexo aún no es
/// obligatorio en todos los ambientes.
/// </summary>
public sealed class clsOpcionesNexo
{
    public const string Seccion = "Nexo";

    /// <summary>
    /// Raíz del servicio Nexo, <b>sin</b> <c>/api</c> ni barra final, ej. <c>https://nexo.boterosoto.com.co</c>: todas las rutas del
    /// contrato de Nexo empiezan por <c>/api/...</c> y el código las agrega (ADR-015 §8).
    /// </summary>
    public string? UrlBase { get; set; }

    /// <summary>
    /// ¿Cada línea de <c>POST /api/captura-solicitudes</c> lleva <c>referenciaRuta</c> (el id de la ruta de Cotifly)? Es la petición N1
    /// a Nexo (#134, <c>docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md</c> §3): se enciende cuando Nexo acepte el campo en
    /// <c>CapturaLineaRequest</c> y lo devuelva en <c>GET /api/solicitudes/{id}</c>. Por defecto <c>false</c>: hoy Nexo no lo tiene y su
    /// swagger declara <c>additionalProperties: false</c>, así que mandarlo antes podría hacer que rechace el alta.
    /// </summary>
    public bool EnviarReferenciaRuta { get; set; }

    /// <summary>
    /// Minutos que se reutiliza en memoria el catálogo de municipios de Nexo (HU-005.13: cambia muy poco y el agente lo consulta
    /// en cada <c>resolver_municipio</c>). Solo se guardan respuestas exitosas; <c>0</c> desactiva la caché. Por defecto 60.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheMunicipios { get; set; } = 60;

    /// <summary>
    /// Minutos que se reutilizan en memoria los catálogos maestros de Nexo (<c>GET /api/catalogos/*</c>, HU-013.10) sin volver a
    /// consultarlos. <c>0</c> desactiva la caché (y la copia de respaldo). Por defecto 30.
    /// </summary>
    [Range(0, 1440)]
    public int MinutosCacheCatalogos { get; set; } = 30;

    /// <summary>
    /// Horas que la última copia de los catálogos maestros sirve de respaldo si Nexo no responde al renovarla (HU-013.10, G2). Pasado
    /// ese límite, sin Nexo, <c>GET /web/catalogos-flete</c> y <c>POST /web/solicitudes-costeo</c> responden
    /// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>. <c>0</c> desactiva el respaldo. Por defecto 24.
    /// </summary>
    [Range(0, 168)]
    public int HorasCatalogosVencidos { get; set; } = 24;
}
