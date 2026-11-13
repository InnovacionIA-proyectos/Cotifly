using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Licitaciones.Interfaces;

/// <summary>
/// Importación inteligente del Excel de licitación con NEXO (HU-044.1, #252, ADR-028). Cotifly no la replica: reenvía a
/// <c>/api/captura-solicitudes/*</c> (<c>sugerir-mapeo</c>, <c>aplicar-mapeo</c>, <c>normalizar-valores</c>, <c>plantillas-mapeo</c>,
/// <c>leer-estructura</c>, <c>recetas-lectura</c>) y a <c>/api/diccionario-cliente</c> de NEXO con la identidad del comercial
/// (<c>X-Portal-clsUsuario-Id</c>) y el cliente por NIT (<c>clienteNit</c>, normalizado como lo compara NEXO). Nada se guarda en Cotifly.
/// </summary>
/// <remarks>
/// <para><b>Errores (nunca lanza por NEXO).</b> Un 400/404 de NEXO con un código conocido llega con ese código y el mensaje de NEXO
/// (<c>DEMASIADAS_COLUMNAS</c>, <c>CAMPO_INVALIDO</c>, <c>RECETA_INVALIDA</c>, <c>CLIENTE_NIT_INVALIDO</c>...), igual que el
/// <c>409 TERMINO_DUPLICADO</c> del diccionario; <c>429 IA_TOPE_DIARIO</c> →
/// <c>IA_TOPE_DIARIO</c> (cupo diario de IA del comercial, sin bloquear a nadie más); 401/403 → <c>NEXO_SIN_PERMISO</c>; red, tiempo
/// agotado, 5xx, circuito abierto, el 429 por minuto o la importación apagada → <c>NEXO_NO_DISPONIBLE</c>.</para>
/// <para><b>Sin IA no es un error.</b> Con la IA de NEXO apagada o caída la respuesta es 200 con <c>iaUsada = false</c> y <c>motivoSinIa</c>.</para>
/// </remarks>
public interface IClsImportacionExcelNexo
{
    /// <summary>¿Está encendida (<c>ImportacionInteligente:Habilitada</c> y <c>Nexo:UrlBase</c>)? Si no, todo responde <c>NEXO_NO_DISPONIBLE</c>.</summary>
    bool EstaHabilitada { get; }

    /// <summary><c>POST sugerir-mapeo</c>: propuesta de mapeo de UNA hoja. Puede gastar IA: nunca se reintenta.</summary>
    Task<clsResultado<clsSugerenciaMapeo>> SugerirMapeoAsync(
        Guid usuarioId, clsPeticionSugerirMapeo peticion, CancellationToken cancellationToken = default);

    /// <summary><c>POST aplicar-mapeo</c>: el mapeo confirmado sobre todas las filas. Sin IA ni efecto en NEXO: se reintenta como un GET.</summary>
    Task<clsResultado<clsResultadoAplicarMapeo>> AplicarMapeoAsync(
        Guid usuarioId, clsPeticionAplicarMapeo peticion, CancellationToken cancellationToken = default);

    /// <summary><c>POST normalizar-valores</c>: los textos libres distintos a valores de NEXO. Puede gastar IA: nunca se reintenta.</summary>
    Task<clsResultado<clsResultadoNormalizacion>> NormalizarValoresAsync(
        Guid usuarioId, string? clienteNit, IReadOnlyList<clsValorANormalizar> valores, CancellationToken cancellationToken = default);

    /// <summary><c>PUT plantillas-mapeo</c>: guarda (o reemplaza) la plantilla del cliente. Sin NIT → <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.</summary>
    Task<clsResultado<clsPlantillaGuardada>> GuardarPlantillaMapeoAsync(
        Guid usuarioId, clsPeticionPlantillaMapeo peticion, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET plantillas-mapeo?clienteNit=</c>: las plantillas del cliente. El NIT es obligatorio: sin él NEXO devolvería las de TODOS los
    /// clientes, así que responde <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsPlantillaMapeo>>> ListarPlantillasMapeoAsync(
        Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE plantillas-mapeo/{id}</c>. <c>PLANTILLA_NO_ENCONTRADA</c> si NEXO no la tiene.</summary>
    Task<clsResultado<bool>> EliminarPlantillaMapeoAsync(Guid usuarioId, Guid plantillaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET diccionario-cliente</c> con <c>clienteNit</c> y los filtros: con NIT, los términos de ese cliente más (si se piden) los
    /// generales; sin NIT, NEXO devuelve los de todos los clientes y filtrar es tarea de quien llama.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsTerminoCliente>>> ObtenerDiccionarioClienteAsync(
        Guid usuarioId, string? clienteNit, clsFiltroDiccionarioCliente? filtro = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST diccionario-cliente</c>: crea el término para el cliente del NIT (obligatorio: sin él NEXO lo crearía general, así que
    /// responde <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO). Nunca se reintenta.
    /// </summary>
    Task<clsResultado<clsTerminoCliente>> CrearTerminoClienteAsync(
        Guid usuarioId, string clienteNit, clsDatosTerminoCliente datos, CancellationToken cancellationToken = default);

    /// <summary><c>PUT diccionario-cliente/{id}</c>: reemplaza los campos editables (el cliente no cambia). Nunca se reintenta.</summary>
    Task<clsResultado<clsTerminoCliente>> EditarTerminoClienteAsync(
        Guid usuarioId, Guid terminoId, clsDatosTerminoCliente datos, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE diccionario-cliente/{id}</c>. <c>TERMINO_NO_ENCONTRADO</c> si NEXO no lo tiene.</summary>
    Task<clsResultado<bool>> EliminarTerminoClienteAsync(Guid usuarioId, Guid terminoId, CancellationToken cancellationToken = default);

    /// <summary><c>GET diccionario-cliente/opciones</c>: tipos, campos de NEXO, unidades y contextos.</summary>
    Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST leer-estructura</c> (multipart): NEXO lee el Excel de formato complejo con una receta (la guardada, la enviada o la de la IA) y
    /// devuelve tablas normales. No guarda nada. Puede gastar IA: nunca se reintenta; usa el tiempo largo (<c>TimeoutLecturaSegundos</c>).
    /// </summary>
    Task<clsResultado<clsLecturaEstructura>> LeerEstructuraAsync(
        Guid usuarioId, clsPeticionLeerEstructura peticion, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>PUT recetas-lectura</c> (multipart): guarda la receta confirmada como formato del cliente. Sin NIT → <c>CLIENTE_REQUERIDO</c> sin llamar
    /// a NEXO. Nunca se reintenta; usa el tiempo largo.
    /// </summary>
    Task<clsResultado<clsRecetaGuardadaResultado>> GuardarRecetaLecturaAsync(
        Guid usuarioId, clsPeticionGuardarReceta peticion, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET recetas-lectura?clienteNit=</c>: los formatos del cliente. El NIT es obligatorio: sin él NEXO devolvería los de TODOS los clientes,
    /// así que responde <c>CLIENTE_REQUERIDO</c> sin llamar a NEXO.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsRecetaGuardada>>> ListarRecetasLecturaAsync(
        Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default);

    /// <summary><c>DELETE recetas-lectura/{id}</c>. <c>RECETA_NO_ENCONTRADA</c> si NEXO no la tiene.</summary>
    Task<clsResultado<bool>> EliminarRecetaLecturaAsync(Guid usuarioId, Guid recetaId, CancellationToken cancellationToken = default);
}
