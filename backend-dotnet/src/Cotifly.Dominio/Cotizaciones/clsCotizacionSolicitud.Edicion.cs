using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-028 (#186, ADR-021): el comercial edita la solicitud. Todo es editable salvo el cliente (NIT, nombre y origen), el <c>tipo</c>
/// (spot/licitación) y la línea de negocio de cada ruta; una ruta nueva solo usa una línea que la solicitud ya tiene. El modo lo decide
/// el estado (<see cref="ModoEdicion"/>).
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: la cotización no nació del formulario de costeo (chat).</summary>
    public const string MotivoEdicionSinFormulario = "SIN_FORMULARIO";

    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: las licitaciones no se editan en esta fase (fase 2 de HU-028).</summary>
    public const string MotivoEdicionLicitacion = "LICITACION";

    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: un envío a Nexo está en curso (<c>pendiente_envio</c> reciente).</summary>
    public const string MotivoEdicionEnvioEnCurso = "ENVIO_EN_CURSO";

    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: alguna ruta espera la cubicación en segundo plano o falló.</summary>
    public const string MotivoEdicionCubicacionEnCurso = "CUBICACION_EN_CURSO";

    /// <summary>
    /// Cómo se aplica una edición en el estado actual, o <c>ESTADO_NO_PERMITE</c> con su motivo (<c>detalle.motivo</c>):
    /// <list type="number">
    /// <item>sin formulario de costeo (chat): <see cref="MotivoEdicionSinFormulario"/>;</item>
    /// <item>licitación: <see cref="MotivoEdicionLicitacion"/> (fase 2);</item>
    /// <item>entregada al cliente o con resultado, o (HU-036.3, #207) finalizada: <see cref="ModoEdicionSolicitud.NuevaVersion"/>;</item>
    /// <item>cubicación en segundo plano en curso o fallida: <see cref="MotivoEdicionCubicacionEnCurso"/>;</item>
    /// <item><c>pendiente_envio</c> reciente (menos de <see cref="EsperaReenvioPendiente"/>): <see cref="MotivoEdicionEnvioEnCurso"/>;</item>
    /// <item>sin <see cref="SolicitudNexoId"/>: <see cref="ModoEdicionSolicitud.EnSitio"/>; con él, <see cref="ModoEdicionSolicitud.Reemplazo"/>
    /// (también en <c>pendiente_costeo</c>: se pierde el trabajo de la Mesa de Costeo, decisión 6).</item>
    /// </list>
    /// </summary>
    public clsResultado<ModoEdicionSolicitud> ModoEdicion(DateTimeOffset ahora)
    {
        if (EstadoCosteo is not { } estado || EncabezadoSolicitud is null)
        {
            return NoEditable(MotivoEdicionSinFormulario, "no nació del formulario de costeo: no tiene una solicitud que editar");
        }

        if (TipoSolicitud == Cotizaciones.TipoSolicitud.Licitacion)
        {
            return NoEditable(MotivoEdicionLicitacion,
                "es una licitación y su edición llega en una fase posterior; mientras tanto, re-solicita el costeo o carga el pliego de nuevo");
        }

        // HU-036.3 (#207, decisión de Sara): la finalizada (precio aceptado) se edita como la entregada, en una versión nueva.
        if (EntregadaAlCliente || Finalizada)
        {
            return ModoEdicionSolicitud.NuevaVersion;
        }

        if (CubicacionEnCurso)
        {
            return NoEditable(MotivoEdicionCubicacionEnCurso, "la cubicación de sus rutas sigue en curso o falló");
        }

        if (estado == Cotizaciones.EstadoCosteo.PendienteEnvio && ahora - UpdatedAt < EsperaReenvioPendiente)
        {
            return NoEditable(MotivoEdicionEnvioEnCurso, "el envío a Nexo está en curso; espera unos minutos");
        }

        return SolicitudNexoId is null ? ModoEdicionSolicitud.EnSitio : ModoEdicionSolicitud.Reemplazo;
    }

    /// <summary>Líneas de negocio (ids de <c>MargenesPolitica</c>) de las rutas guardadas: las únicas que puede usar una ruta nueva.</summary>
    public IReadOnlySet<int> LineasNegocioDeRutas => _rutas.Select(r => r.LineaNegocioId).OfType<int>().ToHashSet();

    /// <summary>
    /// Los campos bloqueados que la edición intenta cambiar (<c>CAMPO_NO_EDITABLE</c>): un campo que no viene (<c>null</c>) o viene con el
    /// mismo valor se acepta (el frontend reenvía el formulario entero). El NIT se compara por sus dígitos y el nombre sin mayúsculas ni
    /// espacios al borde.
    /// </summary>
    public IReadOnlyList<clsErrorCampo> CamposBloqueadosCambiados(
        TipoSolicitud? tipo, string? nitCliente, string? clienteNombre, OrigenCliente? origenCliente)
    {
        var errores = new List<clsErrorCampo>();
        if (tipo is { } nuevoTipo && nuevoTipo != TipoSolicitud)
        {
            errores.Add(Bloqueado("tipo", "El tipo de la solicitud (spot o licitación) no se puede cambiar al editarla."));
        }

        if (nitCliente is not null && Digitos(nitCliente) != Digitos(NitCliente))
        {
            errores.Add(Bloqueado("nit_cliente", "El cliente no se puede cambiar al editar la solicitud: crea una solicitud nueva para otro cliente."));
        }

        if (clienteNombre is not null
            && !string.Equals(clienteNombre.Trim(), ClienteNombre?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errores.Add(Bloqueado("cliente_nombre", "El nombre del cliente no se puede cambiar al editar la solicitud (sale del catálogo de Nexo)."));
        }

        if (origenCliente is { } origen && origen != OrigenCliente)
        {
            errores.Add(Bloqueado("origen_cliente", "El origen del cliente (cliente o prospecto) no se puede cambiar al editar la solicitud."));
        }

        return errores;
    }

    /// <summary>
    /// Aplica la edición en sitio o como reemplazo (HU-028), con las reglas ya validadas por el caso de uso (si no se cumplen, lanza):
    /// <list type="number">
    /// <item>el encabezado (deadline y notas; el <c>tipo</c> no cambia) y, si cambia, la moneda con la TRM del día;</item>
    /// <item>las rutas guardadas que no vienen se quitan (con sus camiones, mercancías y conexos); las que vienen conservan su id, su
    /// línea de negocio y su margen y toman el contenido nuevo; las nuevas se agregan. El orden es el de la edición, y las rutas que se
    /// conservan no cambian su orden relativo (no se reordenan: la base exige un orden único por solicitud);</item>
    /// <item>la solicitud olvida el costeo anterior (<see cref="PrepararReSolicitud"/>: sin ids de Nexo, sin precio, en
    /// <c>pendiente_envio</c>) y recalcula los totales (los conexos ya suman).</item>
    /// </list>
    /// No invalida el PDF ni sube la versión: lo hace el caso de uso.
    /// </summary>
    public clsResultadoEdicion AplicarEdicion(clsEdicionSolicitud edicion, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(edicion);
        ValidarEdicion(edicion);

        RegistrarEncabezado(edicion.Encabezado with { Tipo = TipoSolicitud ?? edicion.Encabezado.Tipo });
        if (edicion.MonedaNueva is { } moneda && !string.Equals(moneda, Moneda, StringComparison.OrdinalIgnoreCase))
        {
            var normalizada = moneda.Trim().ToUpperInvariant();
            if (ValidarTrm(normalizada, edicion.TrmNueva) is { } trm)
            {
                throw new ArgumentException(trm.Mensaje, nameof(edicion));
            }

            Moneda = normalizada;
            TrmAplicada = edicion.TrmNueva;
        }

        var conservadas = edicion.Rutas.Select(r => r.RutaExistenteId).OfType<Guid>().ToHashSet();
        var quitadas = _rutas.Where(r => !conservadas.Contains(r.Id)).ToList();
        foreach (var quitada in quitadas)
        {
            _rutas.Remove(quitada);
        }

        var agregadas = new List<int>();
        for (var i = 0; i < edicion.Rutas.Count; i++)
        {
            var editada = edicion.Rutas[i];
            var orden = i + 1;
            if (editada.RutaExistenteId is { } id)
            {
                _rutas.First(r => r.Id == id).AplicarEdicion(orden, editada.Ruta, edicion.Encabezado, editada.Replanificada, editada.ReincluirRechazados);
                continue;
            }

            _rutas.Add(NuevaRuta(orden, editada.Ruta, edicion.Encabezado));
            agregadas.Add(orden);
        }

        PrepararReSolicitud();
        RecalcularTotales(ahora);
        return new clsResultadoEdicion(quitadas, agregadas);
    }

    /// <summary>
    /// Cabecera de la versión editada <paramref name="folio"/> (<c>-vN</c>, HU-028): la identidad de esta (cliente y comercial) con la
    /// moneda de la edición y la TRM del día. Las rutas las registra el caso de uso (<see cref="RegistrarCaptura"/>).
    /// </summary>
    public clsResultado<clsCotizacionSolicitud> CrearVersionEditada(clsFolio folio, string moneda, decimal trmDelDia)
    {
        ArgumentNullException.ThrowIfNull(folio);
        ArgumentException.ThrowIfNullOrWhiteSpace(moneda);
        if (folio.Version <= Version)
        {
            throw new ArgumentException($"La versión nueva debe ser mayor que {Version}.", nameof(folio));
        }

        return Crear(new clsDatosCotizacion
        {
            Folio = folio.Valor,
            Version = folio.Version,
            ClienteNombre = ClienteNombre,
            NitCliente = NitCliente,
            OrigenCliente = OrigenCliente,
            UsuarioId = UsuarioId,
            Moneda = moneda,
            TrmAplicada = trmDelDia,
        });
    }

    /// <summary>
    /// Versión editada (HU-028, decisión 3): el margen negociado de cada ruta de <paramref name="anterior"/> se copia a la ruta de esta
    /// versión que la edita (<paramref name="rutaAnteriorDe"/>: id de la ruta nueva → id de la ruta original). Las rutas nuevas no tienen.
    /// </summary>
    public void CopiarMargenesNegociadosDe(clsCotizacionSolicitud anterior, IReadOnlyDictionary<Guid, Guid> rutaAnteriorDe)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        ArgumentNullException.ThrowIfNull(rutaAnteriorDe);
        foreach (var ruta in _rutas)
        {
            if (rutaAnteriorDe.TryGetValue(ruta.Id, out var anteriorId) && anterior._rutas.FirstOrDefault(r => r.Id == anteriorId) is { } previa)
            {
                ruta.CopiarMargenNegociado(previa);
            }
        }
    }

    private void ValidarEdicion(clsEdicionSolicitud edicion)
    {
        if (ModoEdicion(DateTimeOffset.MaxValue) is { EsExitoso: true, Valor: ModoEdicionSolicitud.NuevaVersion })
        {
            throw new InvalidOperationException($"La cotización {Folio} ya se entregó al cliente o está finalizada: se edita en una versión nueva.");
        }

        if (edicion.Rutas.Count == 0)
        {
            throw new ArgumentException("La edición debe traer al menos una ruta.", nameof(edicion));
        }

        if (edicion.Encabezado.Errores() is { Count: > 0 } erroresEncabezado)
        {
            throw new ArgumentException(erroresEncabezado[0].Mensaje, nameof(edicion));
        }

        var lineas = LineasNegocioDeRutas;
        var vistos = new HashSet<Guid>();
        var ordenAnterior = 0;
        foreach (var (editada, i) in edicion.Rutas.Select((r, i) => (r, i)))
        {
            ArgumentNullException.ThrowIfNull(editada);
            var capturada = editada.Ruta;
            if (capturada.Linea.Errores().Concat(capturada.Linea.ErroresCaptura()).FirstOrDefault() is { } error)
            {
                throw new ArgumentException($"Ruta {i + 1}: {error.Mensaje}", nameof(edicion));
            }

            if (capturada.Camiones is not { Count: > 0 })
            {
                throw new ArgumentException($"Ruta {i + 1}: debe tener al menos un camión.", nameof(edicion));
            }

            if (editada.RutaExistenteId is not { } id)
            {
                if (capturada.LineaNegocioId is not { } nueva || (lineas.Count > 0 && !lineas.Contains(nueva)))
                {
                    throw new ArgumentException($"Ruta {i + 1}: una ruta nueva solo puede usar una línea de negocio de la solicitud.", nameof(edicion));
                }

                continue;
            }

            var guardada = _rutas.FirstOrDefault(r => r.Id == id)
                ?? throw new ArgumentException($"Ruta {i + 1}: la ruta {id} no es de la cotización {Folio}.", nameof(edicion));
            if (!vistos.Add(id))
            {
                throw new ArgumentException($"Ruta {i + 1}: la ruta {id} viene repetida.", nameof(edicion));
            }

            if (capturada.LineaNegocioId != guardada.LineaNegocioId)
            {
                throw new ArgumentException($"Ruta {i + 1}: la línea de negocio de una ruta no se cambia.", nameof(edicion));
            }

            if (guardada.Orden < ordenAnterior)
            {
                throw new ArgumentException($"Ruta {i + 1}: las rutas que se conservan no se reordenan.", nameof(edicion));
            }

            ordenAnterior = guardada.Orden;
        }
    }

    private clsLineaSolicitud NuevaRuta(int orden, clsRutaCapturada capturada, clsEncabezadoSolicitudCosteo encabezado)
    {
        var ruta = clsLineaSolicitud.Desde(Id, orden, orden - 1, capturada.LineaNegocioId, capturada.Linea, encabezado, capturada.RutaId);
        foreach (var camion in capturada.Camiones)
        {
            ruta.AgregarVehiculo(camion);
        }

        ruta.RegistrarCubicacion(capturada.Cubicacion, capturada.AvisosVehiculo);
        foreach (var conexo in capturada.Conexos ?? [])
        {
            ruta.AgregarOActualizarConexo(conexo);
        }

        return ruta;
    }

    private static clsErrorCampo Bloqueado(string campo, string mensaje) => new(campo, clsCodigosError.CampoNoEditable, mensaje);

    private static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private clsErrorDominio NoEditable(string motivo, string razon) =>
        new(clsCodigosError.EstadoNoPermite, $"La solicitud {Folio} no se puede editar: {razon}.", motivo);
}
