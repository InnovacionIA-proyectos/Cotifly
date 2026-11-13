namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Un mensaje del comercial para el asistente por folio: la sesión queda ligada al comercial y al folio.
/// </summary>
/// <param name="Mensaje">Texto del comercial (máximo 8000 caracteres).</param>
/// <param name="SpaceId">Agrupa la conversación de la consola (<c>web/{sesion_id}</c>).</param>
/// <param name="ThreadId">Hilo de ESTA cotización (<c>web/{sesion_id}/{folio}</c>): otro folio con el mismo <c>sesion_id</c> es otro hilo.</param>
/// <param name="UsuarioId">Comercial del token (nunca del LLM).</param>
/// <param name="Folio">Cotización sobre la que trabaja el asistente; debe ser del comercial.</param>
public sealed record clsSolicitudTurnoFolio(string Mensaje, string SpaceId, string ThreadId, Guid UsuarioId, string Folio);

/// <summary>Salida de un turno del asistente (port de <c>ResultadoTurno</c> de <c>orquestador/agente.py</c>).</summary>
/// <param name="Texto">Respuesta final para el comercial (redactada en la llamada final sin herramientas y validada).</param>
/// <param name="Iteraciones">Llamadas al modelo del turno (incluida la llamada final).</param>
/// <param name="HerramientasInvocadas">Herramientas ejecutadas, en orden (las inexistentes que pidió el modelo no cuentan).</param>
/// <param name="DetenerPor">Motivo de fin del loop que informó el proveedor (<c>stop</c>, <c>length</c>...), o <c>propuesta</c> si una ronda
/// de herramientas armó una propuesta y el turno pasó directo a la respuesta final.</param>
/// <param name="Salidas">Resultado de cada herramienta pedida, en orden (incluye las rechazadas y las inexistentes).</param>
public sealed record clsResultadoTurno(
    string Texto,
    int Iteraciones,
    IReadOnlyList<string> HerramientasInvocadas,
    string DetenerPor,
    IReadOnlyList<clsResultadoHerramienta> Salidas)
{
    /// <summary>
    /// Respuesta final estructurada y validada: tipo de interacción, confirmación y acciones sugeridas. Si no se
    /// indica, una respuesta conversacional con <see cref="Texto"/>.
    /// </summary>
    public clsRespuestaAgenteEstructurada Respuesta { get; init; } =
        new() { Tipo = TipoRespuestaAgente.Conversacional, Texto = Texto, RequiereConfirmacion = false };

    /// <summary>
    /// True si el modelo no produjo una respuesta válida ni con el reintento y <see cref="Texto"/> es el respaldo controlado del
    /// validador: <c>/web/chat</c> lo muestra como error (<c>200 {texto, error: true}</c>) si no hubo propuesta.
    /// </summary>
    public bool EsRespaldo { get; init; }

    /// <summary>Reintentos de la respuesta final por validación fallida (0 o 1).</summary>
    public int ReintentosRespuesta { get; init; }

    /// <summary>
    /// Acción que el asistente por folio PROPUSO en el turno, armada por el servidor, o <c>null</c>.
    /// </summary>
    public clsAccionPropuesta? AccionPropuesta { get; init; }

    /// <summary>
    /// De dónde salió la respuesta final: <c>loop</c> (la respuesta sin herramientas ya era válida),
    /// <c>llamada_final</c> (la llamada aparte del validador) o <c>null</c> en un turno que no llegó a cerrarse.
    /// </summary>
    public string? Cierre { get; init; }
}

/// <summary>
/// Nombres de las herramientas tal como las ve el modelo (los del Python, en snake_case). Los parámetros van en camelCase (firma
/// unificada del plan: <c>lineaNegocio</c>, <c>pesoKg</c>...). Del plugin <c>cotizador</c> solo quedan los nombres que se usan:
/// <c>consultar_cotizacion</c> la usa el asistente por folio y las otras se citan en las acciones sugeridas.
/// </summary>
public static class clsNombresHerramientas
{
    public const string Plugin = "cotizador";
    public const string RegistrarAjusteMargen = "registrar_ajuste_margen";
    public const string GenerarPdf = "generar_pdf";
    public const string ConsultarCotizacion = "consultar_cotizacion";

    /// <summary>Plugin del asistente por folio: solo lectura y propuesta.</summary>
    public const string PluginAsistente = "asistente";
    public const string ProponerAjusteMargen = "proponer_ajuste_margen";
    public const string ProponerReSolicitud = "proponer_re_solicitud";
    public const string BuscarServicioConexo = "buscar_servicio_conexo";
    public const string ProponerAjusteMargenRutas = "proponer_ajuste_margen_rutas";
    public const string ProponerAgregarConexoRutas = "proponer_agregar_conexo_rutas";
    public const string ProponerQuitarConexoRutas = "proponer_quitar_conexo_rutas";
}
