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
    /// Límite de cada intento de las consultas al cubicador (segundos): los catálogos y la sonda de salud. Corto a propósito: un GET que no
    /// responde se reintenta. Por defecto 15. D3b/D7 (E2E 2026-10-16): el <c>POST /recomendaciones</c> tiene su propio límite,
    /// <see cref="TiempoEsperaRecomendacionSegundos"/>, y su reconsulta también (<see cref="TiempoEsperaReconsultaSegundos"/>, opción C).
    /// </summary>
    [Range(1, 120)]
    public int TiempoEsperaSegundos { get; set; } = 15;

    /// <summary>
    /// D3b (E2E 2026-10-16): límite del <c>POST /recomendaciones</c> (segundos). El motor del cubicador tarda 26 a 31 s con cargas grandes
    /// (1.100 unidades) y bloquea su API mientras calcula (un solo proceso, cálculo síncrono), así que el límite general
    /// (<see cref="TiempoEsperaSegundos"/>) dejaba esas cargas en el respaldo o en 503. Un POST nunca se reintenta: sin respuesta se
    /// reconsulta con GET (<see cref="EsperaAntesReconsultaSegundos"/>, <see cref="TiempoEsperaReconsultaSegundos"/>). Opción C: por
    /// defecto 90; de 1 a 300 (el <c>HttpClient</c> del cubicador no tiene límite propio: manda la tubería). Al arrancar se valida que no
    /// sea menor que <see cref="TiempoEsperaSegundos"/> ni mayor que <see cref="PresupuestoCapturaSegundos"/>.
    /// </summary>
    [Range(1, 300)]
    public int TiempoEsperaRecomendacionSegundos { get; set; } = 90;

    /// <summary>
    /// Opción C: espera antes de reconsultar con <c>GET /recomendaciones</c> un POST que no respondió (segundos). El cubicador atiende las
    /// peticiones en orden: si el motor sigue calculando, el GET se queda en la fila y responde cuando termina. Por defecto 5; de 0 a 60.
    /// </summary>
    [Range(0, 60)]
    public int EsperaAntesReconsultaSegundos { get; set; } = 5;

    /// <summary>
    /// Opción C: límite de la reconsulta <c>GET /recomendaciones</c> tras un POST sin respuesta (segundos, reintentos incluidos). Es largo a
    /// propósito: el cubicador responde ese GET cuando el motor termina. Por defecto 60; de 1 a 300; al arrancar se valida que no sea menor
    /// que <see cref="TiempoEsperaSegundos"/>. La huella de la corrida se sigue comprobando igual.
    /// </summary>
    [Range(1, 300)]
    public int TiempoEsperaReconsultaSegundos { get; set; } = 60;

    /// <summary>
    /// Opción C: llamadas a <c>POST /recomendaciones</c> en curso a la vez desde este proceso, para todo Cotifly (cubicación rápida,
    /// captura, recálculo y licitación). El POST y su reconsulta ocupan el mismo cupo. Por defecto 1: el cubicador calcula una carga a la
    /// vez y una segunda solo espera en su fila. Subirlo cuando el cubicador calcule fuera de su bucle de eventos (opción F). De 1 a 64.
    /// </summary>
    [Range(1, 64)]
    public int MaximoConcurrente { get; set; } = 1;

    /// <summary>
    /// Opción C: cuánto espera una llamada a que se libere el cupo (<see cref="MaximoConcurrente"/>) antes de rendirse con
    /// <c>CUBICADOR_OCUPADO</c> (segundos). La espera cuenta dentro del tiempo de quien llama (el presupuesto de la captura, el reclamo de
    /// la licitación) y se cancela con él. Por defecto 60 (lo que tardan dos cargas grandes); de 0 a 600 (0 = no espera).
    /// </summary>
    [Range(0, 600)]
    public int EsperaMaximaCupoSegundos { get; set; } = 60;

    /// <summary>
    /// Opción D: cuánto espera <c>POST /web/cubicacion</c> al cubicador antes de responder 202 y seguir en segundo plano (segundos). Si el
    /// cálculo termina antes, responde 200 como siempre. Por defecto 8; de 0 a 30 (0 = siempre 202).
    /// </summary>
    [Range(0, 30)]
    public int EsperaSincronaCubicacionSegundos { get; set; } = 8;

    /// <summary>
    /// Opción D: minutos que se guarda en memoria una cubicación rápida (en proceso o terminada) para <c>GET /web/cubicacion/{id}</c>.
    /// Por defecto 15; de 1 a 1440. Si el proceso se reinicia, se pierden (el front recibe 404 y vuelve a cubicar).
    /// </summary>
    [Range(1, 1440)]
    public int RetencionCubicacionMinutos { get; set; } = 15;

    /// <summary>
    /// Opción D: cubicaciones rápidas guardadas en memoria como máximo. Lleno, se descartan primero las terminadas más viejas; si todas
    /// siguen en proceso, <c>POST /web/cubicacion</c> responde 503 <c>CUBICADOR_OCUPADO</c>. Por defecto 500; de 1 a 100.000.
    /// </summary>
    [Range(1, 100_000)]
    public int MaximoCubicacionesEnMemoria { get; set; } = 500;

    /// <summary>Minutos que se reutiliza en memoria el catálogo de tipos de mercancía (solo respuestas exitosas). <c>0</c> la desactiva. Por defecto 30.</summary>
    [Range(0, 1440)]
    public int MinutosCacheCatalogos { get; set; } = 30;

    /// <summary>
    /// Si el cubicador configurado falla en la captura (caído, sin respuesta a tiempo, respuesta inválida), ¿la ruta sigue con un camión
    /// sin vehículo (respaldo, con un warning en el log)? Por defecto <c>true</c> (regla del coordinador, ADR-019): el cubicador real ya está
    /// conectado y una caída no detiene la captura; con <c>Captura:AceptarVehiculoCliente</c> ese camión lleva el vehículo digitado. Con
    /// <c>false</c> la captura falla con <c>CUBICADOR_NO_DISPONIBLE</c> y no se guarda ni se envía nada a Nexo (regla de la issue #133).
    /// </summary>
    public bool RespaldoSiFalla { get; set; } = true;

    /// <summary>
    /// Tiempo máximo de llamadas al cubicador en una misma captura (segundos; una licitación hace un POST por fila). Se revisa antes de
    /// cada llamada: pasado, las rutas restantes no lo llaman y van al respaldo o, con <see cref="RespaldoSiFalla"/> = <c>false</c>, la
    /// captura falla con 503. La espera por el cupo (<see cref="EsperaMaximaCupoSegundos"/>) cuenta dentro. Por defecto 180 (opción C:
    /// caben una espera por el cupo y dos recomendaciones lentas); debe ser al menos <see cref="TiempoEsperaRecomendacionSegundos"/>, para
    /// que una sola ruta lenta no agote la captura (se valida al arrancar).
    /// </summary>
    [Range(1, 3600)]
    public int PresupuestoCapturaSegundos { get; set; } = 180;

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
