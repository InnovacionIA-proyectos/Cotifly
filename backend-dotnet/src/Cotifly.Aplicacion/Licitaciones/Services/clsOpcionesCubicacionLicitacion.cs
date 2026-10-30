using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// HU-023.5 (#160): cubicación de la licitación en segundo plano. Comparte la sección <c>Cubicador</c> con la conexión al cubicador
/// (<c>clsOpcionesCubicador</c>, en Infraestructura); estas son las claves del proceso en segundo plano, validadas al arrancar.
/// </summary>
public sealed class clsOpcionesCubicacionLicitacion
{
    public const string Seccion = "Cubicador";

    /// <summary>
    /// Con el cubicador configurado, ¿la licitación se guarda sin esperar al cubicador y sus rutas se cubican en segundo plano? Por defecto
    /// <c>true</c>. Con <c>false</c> vuelve el comportamiento de HU-023.3 (todas las rutas mientras el comercial espera, todo o nada).
    /// </summary>
    public bool LicitacionEnSegundoPlano { get; set; } = true;

    /// <summary>
    /// ¿Esta réplica procesa la cola? Por defecto <c>true</c>. Sirve para dejar una sola réplica con el proceso o apagarlo en pruebas (la
    /// licitación se sigue guardando; la procesa otra réplica).
    /// </summary>
    public bool ProcesadorHabilitado { get; set; } = true;

    /// <summary>
    /// Petición P6: llamadas al cubicador en paralelo como máximo, <b>entre todas las réplicas</b> (un cupo por llamada con
    /// <c>sp_getapplock</c> en la base). Opción C (D3, E2E 2026-10-16): por defecto 1, porque el cubicador calcula una carga a la vez; al
    /// arrancar se valida que no supere el cupo global de este proceso (<c>Cubicador:MaximoConcurrente</c>): más rutas en paralelo solo
    /// esperarían ese cupo con la ruta ya reclamada.
    /// </summary>
    [Range(1, 64)]
    public int MaximoParalelo { get; set; } = 1;

    /// <summary>Cada cuánto revisa la cola cada réplica (s).</summary>
    [Range(1, 300)]
    public int IntervaloTickSegundos { get; set; } = 5;

    /// <summary>Rutas que lee una réplica por vuelta como máximo.</summary>
    [Range(1, 1000)]
    public int LoteRutas { get; set; } = 50;

    /// <summary>
    /// Cuánto tiene una réplica la ruta reclamada (s). Pasado, otra la puede tomar (la réplica se cayó a mitad). Debe ser mayor que lo que
    /// puede durar la llamada de la ruta más 30 s de margen (opción C: la espera por el cupo global, <c>Cubicador:EsperaMaximaCupoSegundos</c>
    /// = 60; el POST, <c>Cubicador:TiempoEsperaRecomendacionSegundos</c> = 90 + 2; la espera antes de la reconsulta, 5, y la reconsulta,
    /// <c>Cubicador:TiempoEsperaReconsultaSegundos</c> = 60: 247 con los valores por defecto): una llamada en curso no se repite en otra
    /// réplica. Por defecto 300.
    /// </summary>
    [Range(31, 3600)]
    public int ReclamoSegundos { get; set; } = 300;

    /// <summary>
    /// Intentos por ruta cuando el cubicador está caído (sin respaldo, <c>CUBICADOR_NO_DISPONIBLE</c>). Agotados, la ruta queda
    /// <c>fallida</c>. Un rechazo de la petición o una respuesta inválida no se reintenta: falla al primer intento.
    /// </summary>
    [Range(1, 20)]
    public int MaximoIntentos { get; set; } = 3;

    /// <summary>Espera antes del primer reintento (s); se duplica en cada intento.</summary>
    [Range(1, 3600)]
    public int EsperaReintentoSegundos { get; set; } = 30;

    public TimeSpan Reclamo => TimeSpan.FromSeconds(ReclamoSegundos);

    /// <summary>Espera antes del reintento número <paramref name="intento"/> (1, 2, ...): la base duplicada en cada intento, hasta 1 h.</summary>
    public TimeSpan EsperaReintento(int intento) =>
        TimeSpan.FromSeconds(Math.Min(3600d, EsperaReintentoSegundos * Math.Pow(2, Math.Max(0, intento - 1))));
}
