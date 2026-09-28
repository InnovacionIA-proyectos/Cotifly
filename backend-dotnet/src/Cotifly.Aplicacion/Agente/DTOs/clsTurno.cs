namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Un mensaje del comercial para el agente, venga de la consola web o de Google Chat.
/// </summary>
/// <param name="Mensaje">Texto del comercial (máximo 8000 caracteres, contrato <c>MensajeWeb</c>).</param>
/// <param name="SpaceId">Space de Google Chat, o <c>web/{sesion_id}</c> en la consola web (ADR-004 §6).</param>
/// <param name="ThreadId">Hilo de Google Chat, o <c>web/{sesion_id}</c> en la consola web.</param>
/// <param name="UsuarioId">Comercial en <c>UsuariosWeb</c> (del token en la web, del email en Google Chat, ADR-011). Nunca del LLM.</param>
/// <param name="CotizacionOrigenId">
/// Cotización del comercial de la que parte una nueva versión (<c>cotizacion_origen_id</c> de <c>MensajeWeb</c>): siembra el estado para
/// que <c>generar_pdf</c> cree <c>-vN</c>. Se valida el dueño.
/// </param>
/// <param name="Moneda">
/// Moneda del selector del formulario web (<c>COP</c> | <c>USD</c>, <c>moneda</c> de <c>MensajeWeb</c>, HU-012.2), ya validada por el canal; <c>null</c>
/// si no vino. Queda en el estado y manda sobre la que pida el modelo.
/// </param>
public sealed record clsSolicitudTurno(string Mensaje, string SpaceId, string ThreadId, Guid UsuarioId, Guid? CotizacionOrigenId = null, string? Moneda = null);

/// <summary>Salida de un turno del agente (port de <c>ResultadoTurno</c> de <c>orquestador/agente.py</c>).</summary>
/// <param name="Texto">Respuesta final para el comercial (redactada en la llamada final sin herramientas y validada).</param>
/// <param name="Iteraciones">Llamadas al modelo del turno (incluida la llamada final).</param>
/// <param name="HerramientasInvocadas">Herramientas ejecutadas, en orden (las inexistentes que pidió el modelo no cuentan).</param>
/// <param name="DetenerPor">Motivo de fin del loop que informó el proveedor (<c>stop</c>, <c>length</c>...).</param>
/// <param name="Salidas">Resultado de cada herramienta pedida, en orden (incluye las rechazadas y las inexistentes).</param>
public sealed record clsResultadoTurno(
    string Texto,
    int Iteraciones,
    IReadOnlyList<string> HerramientasInvocadas,
    string DetenerPor,
    IReadOnlyList<clsResultadoHerramienta> Salidas)
{
    /// <summary>
    /// Respuesta final estructurada y validada (HU-003.10, #16): tipo de interacción, confirmación y acciones sugeridas. Si no se
    /// indica, una respuesta conversacional con <see cref="Texto"/>.
    /// </summary>
    public clsRespuestaAgenteEstructurada Respuesta { get; init; } =
        new() { Tipo = TipoRespuestaAgente.Conversacional, Texto = Texto, RequiereConfirmacion = false };

    /// <summary>
    /// True si el modelo no produjo una respuesta válida ni con el reintento y <see cref="Texto"/> es el respaldo controlado del
    /// validador: los canales lo muestran como error (<c>200 {texto, error: true}</c> en <c>/web/chat</c>).
    /// </summary>
    public bool EsRespaldo { get; init; }

    /// <summary>Reintentos de la respuesta final por validación fallida (0 o 1).</summary>
    public int ReintentosRespuesta { get; init; }

    /// <summary>
    /// Última salida de la herramienta <paramref name="herramienta"/> en el turno si terminó bien y es del tipo pedido (como
    /// <c>ultima_salida</c> + <c>_ok</c> del Python: si la última terminó con error, no hay salida).
    /// </summary>
    public TSalida? UltimaSalida<TSalida>(string herramienta) where TSalida : class
    {
        for (var i = Salidas.Count - 1; i >= 0; i--)
        {
            var salida = Salidas[i];
            if (salida.Herramienta == herramienta)
            {
                return salida.EsExitoso ? salida.Salida as TSalida : null;
            }
        }

        return null;
    }
}

/// <summary>
/// Nombres de las herramientas del plugin <c>cotizador</c> tal como las ve el modelo (los del Python, en snake_case). Los
/// parámetros van en camelCase (firma unificada del plan: <c>lineaNegocio</c>, <c>pesoKg</c>...).
/// </summary>
public static class clsNombresHerramientas
{
    public const string Plugin = "cotizador";
    public const string BuscarCliente = "buscar_cliente";
    public const string BuscarTarifa = "buscar_tarifa";
    public const string ObtenerMargen = "obtener_margen";
    public const string CalcularPrecio = "calcular_precio";
    public const string RegistrarAjusteMargen = "registrar_ajuste_margen";
    public const string GenerarPdf = "generar_pdf";
    public const string EnviarCorreo = "enviar_correo";
    public const string ConsultarCotizacion = "consultar_cotizacion";
}
