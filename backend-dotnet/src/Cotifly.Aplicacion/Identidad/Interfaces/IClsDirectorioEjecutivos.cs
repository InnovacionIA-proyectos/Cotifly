namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// Puerto de solo lectura hacia la tabla corporativa de ejecutivos con su regional (<c>DWCubosEBSSAP.DimEjecutivo</c>,
/// ADR-016 §6). El usuario se identifica por su <c>nombreUsuario</c> de ClientePro (el mismo de <c>UsuariosWeb</c>); el
/// adaptador quita el prefijo de dominio (<c>boterosoto\</c>) de la columna de la tabla antes de comparar (#81).
/// Los fallos de la base se propagan: quien llama decide (el tablero responde 503, fail-closed; el login sigue sin refrescar
/// la regional).
/// </summary>
public interface IClsDirectorioEjecutivos
{
    /// <summary><c>false</c> si no hay conexión configurada (<c>ConnectionStrings:Regional</c> vacía): no se consulta nada.</summary>
    bool Configurado { get; }

    /// <summary>
    /// Límite total de la consulta cuando se hace durante el login (<c>Regional:TiempoEsperaLoginSegundos</c>, acotado por
    /// <c>Regional:TiempoEsperaSegundos</c>). El login lo aplica con su propia cancelación para no quedar esperando a la fuente.
    /// </summary>
    TimeSpan LimiteLogin { get; }

    /// <summary>
    /// Regional de cada <paramref name="nombresUsuario"/> que aparece en la tabla (claves sin distinguir mayúsculas). Un
    /// usuario con dos regionales distintas no se incluye (no se adivina cuál vale).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ObtenerRegionalesAsync(IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default);

    /// <summary><c>nombreUsuario</c> (sin dominio) de los ejecutivos de <paramref name="regional"/> (sin distinguir mayúsculas).</summary>
    Task<IReadOnlyList<string>> ListarNombresUsuarioAsync(string regional, CancellationToken cancellationToken = default);
}
