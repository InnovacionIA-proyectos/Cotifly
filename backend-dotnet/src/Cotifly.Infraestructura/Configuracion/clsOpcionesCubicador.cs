using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Cubicador (motor de selección de vehículos; sección <c>Cubicador</c>, HU-021.5, #133). Sin <see cref="UrlBase"/> (valor por defecto,
/// porque el cubicador real aún no tiene <c>POST /recomendaciones</c>, C1) se registra <c>clsCubicadorNoDisponible</c> y la captura crea un
/// camión sin vehículo por ruta. Con <see cref="UrlBase"/>, <see cref="ApiKey"/> es obligatoria: viaja como
/// <c>Authorization: Bearer {ApiKey}</c> (C5). Reglas en <see cref="clsValidadorOpcionesCubicador"/>; ningún mensaje ni log incluye la llave.
/// </summary>
public sealed class clsOpcionesCubicador
{
    public const string Seccion = "Cubicador";

    /// <summary>
    /// Raíz del API del cubicador, sin barra final (ej. <c>https://cubicador.boterosoto.com.co/api</c>): el código agrega
    /// <c>/recomendaciones</c>, <c>/tipos-mercancia</c>, <c>/tipos-carroceria</c> y <c>/tipos-vehiculo</c>. Vacía = cubicador no configurado.
    /// </summary>
    public string? UrlBase { get; set; }

    /// <summary>Llave del cubicador (<c>CUBICADOR_API_KEY</c>), obligatoria con <see cref="UrlBase"/>. En Production, al menos 16 caracteres.</summary>
    [clsSecreto(Opcional = true)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Límite de cada intento de las consultas al cubicador (segundos): los catálogos, la reconsulta <c>GET /recomendaciones</c> y la
    /// sonda de salud. Corto a propósito: un GET que no responde se reintenta. Por defecto 15. D3b/D7 (E2E 2026-10-16): el
    /// <c>POST /recomendaciones</c> tiene su propio límite, <see cref="TiempoEsperaRecomendacionSegundos"/>.
    /// </summary>
    [Range(1, 120)]
    public int TiempoEsperaSegundos { get; set; } = 15;

    /// <summary>
    /// D3b (E2E 2026-10-16): límite del <c>POST /recomendaciones</c> (segundos). El motor del cubicador tarda 26 a 31 s con cargas grandes
    /// (1.100 unidades), así que el límite general (<see cref="TiempoEsperaSegundos"/>) dejaba esas cargas en el respaldo o en 503. Un POST
    /// nunca se reintenta: sin respuesta se reconsulta con GET. Por defecto 60; de 1 a 90 (por debajo de los 100 s del <c>HttpClient</c>),
    /// nunca menor que <see cref="TiempoEsperaSegundos"/> ni mayor que <see cref="PresupuestoCapturaSegundos"/> (se valida al arrancar).
    /// </summary>
    [Range(1, 90)]
    public int TiempoEsperaRecomendacionSegundos { get; set; } = 60;

    /// <summary>Minutos que se reutiliza en memoria el catálogo de tipos de mercancía (solo respuestas exitosas). <c>0</c> la desactiva. Por defecto 30.</summary>
    [Range(0, 1440)]
    public int MinutosCacheCatalogos { get; set; } = 30;

    /// <summary>
    /// Si el cubicador configurado falla en la captura (caído, sin respuesta a tiempo, respuesta inválida), ¿la ruta sigue con un camión
    /// sin vehículo (respaldo, con un warning en el log)? Por defecto <c>true</c> mientras el cubicador real no exista (C1). Con
    /// <c>false</c> la captura falla con <c>CUBICADOR_NO_DISPONIBLE</c> y no se guarda ni se envía nada a Nexo (regla de la issue #133).
    /// </summary>
    public bool RespaldoSiFalla { get; set; } = true;

    /// <summary>
    /// Tiempo máximo de llamadas al cubicador en una misma captura (segundos; una licitación hace un POST por fila). Se revisa antes de
    /// cada llamada: pasado, las rutas restantes no lo llaman y van al respaldo o, con <see cref="RespaldoSiFalla"/> = <c>false</c>, la
    /// captura falla con 503. Por defecto 120 (D3b: caben dos recomendaciones lentas); debe ser al menos
    /// <see cref="TiempoEsperaRecomendacionSegundos"/>, para que una sola ruta lenta no agote la captura (se valida al arrancar).
    /// </summary>
    [Range(1, 3600)]
    public int PresupuestoCapturaSegundos { get; set; } = 120;

    /// <summary>
    /// Tope total (segundos, reintentos incluidos) de <c>GET /tipos-mercancia</c>. Agotado, el catálogo cuenta como no disponible: la
    /// captura conserva el nombre que mandó el cliente y <c>GET /web/tipos-mercancia</c> responde 503. Por defecto 10.
    /// </summary>
    [Range(1, 120)]
    public int TiempoMaximoCatalogoSegundos { get; set; } = 10;

    /// <summary>
    /// Ruta del endpoint de salud del cubicador bajo <see cref="UrlBase"/> (petición P7, por ejemplo <c>/health</c>). Vacía (por
    /// defecto, mientras el cubicador no lo publique) = <c>/health/ready</c> no consulta el cubicador. Con valor, la sonda
    /// <c>cubicador</c> hace un GET liviano: si falla y <see cref="RespaldoSiFalla"/> es <c>false</c>, Cotifly no está listo; con el
    /// respaldo encendido solo queda un warning (la captura sigue con camiones sin vehículo).
    /// </summary>
    public string? RutaSalud { get; set; }
}
