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
    /// <c>/recomendaciones</c> y <c>/tipos-mercancia</c>. Vacía = cubicador no configurado.
    /// </summary>
    public string? UrlBase { get; set; }

    /// <summary>Llave del cubicador (<c>CUBICADOR_API_KEY</c>), obligatoria con <see cref="UrlBase"/>. En Production, al menos 16 caracteres.</summary>
    [clsSecreto(Opcional = true)]
    public string? ApiKey { get; set; }

    /// <summary>Límite de cada petición al cubicador (segundos). Un POST nunca se reintenta. Por defecto 15.</summary>
    [Range(1, 120)]
    public int TiempoEsperaSegundos { get; set; } = 15;

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
    /// Tiempo máximo de llamadas al cubicador en una misma captura (segundos; una licitación hace un POST por fila). Pasado, las rutas
    /// restantes no lo llaman: van al respaldo o, con <see cref="RespaldoSiFalla"/> = <c>false</c>, la captura falla con 503. Por defecto 60.
    /// </summary>
    [Range(1, 3600)]
    public int PresupuestoCapturaSegundos { get; set; } = 60;
}
