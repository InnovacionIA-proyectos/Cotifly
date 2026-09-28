using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Aplicacion.Tarifas.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Sesiones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Arnés del agente para pruebas unitarias: runner real, herramientas reales y estado real sobre casos de uso simulados
/// (NSubstitute), sesiones en memoria y el modelo guionizado. Margen por defecto: <c>Nacional</c> 13,5 % de la política; con el BP
/// <c>15927</c>, 12 % de SAP TM. <c>buscar_cliente</c> responde el stub (<c>CLIENTE_BUSQUEDA_NO_DISPONIBLE</c>) salvo que la prueba
/// lo cambie.
/// </summary>
internal sealed class clsArnesAgente
{
    public const string Hilo = "web/sesion-prueba-001";
    public const string BpLongyin = "15927";
    public const string NitLongyin = "900123456";

    public clsArnesAgente()
    {
        Usuario = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas Garcia", "nvargas@boterosoto.com.co", null, Reloj.AhoraUtc).Valor;
        Usuarios.ObtenerPorIdAsync(Usuario.Id, Arg.Any<CancellationToken>()).Returns(Usuario);
        Calendario.Hoy.Returns(new DateOnly(2026, 9, 24));

        Buscador.BuscarAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoBusquedaClientes>.Fallo(new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, "Búsqueda no disponible.")));
        Tarifas.BuscarAsync(Arg.Any<clsEntradaBuscarTarifa>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaTarifa>.Fallo(new clsErrorDominio(clsCodigosError.TarifaFuenteNoDisponible, "Sin fuente de tarifas.")));
        Margen.ResolverAsync(Arg.Any<clsEntradaMargen>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var entrada = ci.Arg<clsEntradaMargen>();
            EntradasMargen.Add(entrada);
            return entrada.BpCliente == BpLongyin
                ? clsResultado<clsMargenResuelto>.Exito(new clsMargenResuelto(new clsSalidaMargen(0.12m, "sap_tm", "OPTN", false, "Margen propuesto SAP TM para cliente 15927 en OPTN: 12%"), "Nacional", 1, 0.135m, "margen"))
                : clsResultado<clsMargenResuelto>.Exito(new clsMargenResuelto(new clsSalidaMargen(0.135m, "politica_linea", "OPTN", false, "Política de la línea"), "Nacional", 1, 0.135m, "margen"));
        });
        Pdf.GenerarAsync(Arg.Any<clsEntradaGenerarPdf>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var entrada = ci.Arg<clsEntradaGenerarPdf>();
            EntradasPdf.Add(entrada);
            return clsResultado<clsSalidaPdf>.Exito(new clsSalidaPdf(CotizacionGenerada, entrada.Folio, $"/pdfs/{entrada.Folio}.pdf", true));
        });
        Folios.CrearConFolioNuevoAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsSalidaPdf>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                NitsFolio.Add(ci.ArgAt<string?>(1));
                var folio = clsFolio.Crear(new DateOnly(2026, 9, 24), "NV", 1, ci.ArgAt<string?>(1)).Valor;
                return ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsSalidaPdf>>>>()(folio, CancellationToken.None);
            });
        Folios.CrearNuevaVersionAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsSalidaPdf>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                OrigenesVersion.Add(ci.ArgAt<Guid>(1));
                var folio = clsFolio.Desde("PCN-NV-24092026-001").Valor.ConVersion(2).Valor;
                return ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsSalidaPdf>>>>()(folio, CancellationToken.None);
            });
    }

    public List<Guid> OrigenesVersion { get; } = [];

    public Guid CotizacionGenerada { get; } = Guid.NewGuid();

    public clsChatClientGuionizado Modelo { get; } = new();

    public clsSesionRepositoryMemoria Sesiones { get; } = new();

    public IClsUsuarioWebRepository Usuarios { get; } = Substitute.For<IClsUsuarioWebRepository>();

    public IClsUnitOfWork Unidad { get; } = Substitute.For<IClsUnitOfWork>();

    public clsRelojFalso Reloj { get; } = new();

    public IClsCalendarioNegocio Calendario { get; } = Substitute.For<IClsCalendarioNegocio>();

    public IClsAuditoriaService Auditoria { get; } = Substitute.For<IClsAuditoriaService>();

    public IClsBuscadorClientes Buscador { get; } = Substitute.For<IClsBuscadorClientes>();

    public IClsTarifaProvider Tarifas { get; } = Substitute.For<IClsTarifaProvider>();

    public IClsMargenResolverService Margen { get; } = Substitute.For<IClsMargenResolverService>();

    public IClsAjusteMargenService Ajuste { get; } = Substitute.For<IClsAjusteMargenService>();

    public IClsGenerarPdfService Pdf { get; } = Substitute.For<IClsGenerarPdfService>();

    public IClsGeneradorFolioService Folios { get; } = Substitute.For<IClsGeneradorFolioService>();

    public IClsCorreoService Correo { get; } = Substitute.For<IClsCorreoService>();

    public IClsCotizacionRepository Cotizaciones { get; } = Substitute.For<IClsCotizacionRepository>();

    /// <summary>Validador de la respuesta final; por defecto el real (HU-003.10) con <see cref="OpcionesAgente"/>.</summary>
    public IClsValidadorRespuestaAgente? Validador { get; set; }

    public clsCandadoSesionFalso Candado { get; } = new();

    public IClsMargenPoliticaRepository Politicas { get; } = Substitute.For<IClsMargenPoliticaRepository>();

    public clsOpcionesAgente OpcionesAgente { get; } = new();

    public clsOpcionesSesiones OpcionesSesiones { get; } = new();

    public clsOpcionesCorreo OpcionesCorreo { get; } = new();

    public clsUsuarioWeb Usuario { get; }

    public List<clsEntradaMargen> EntradasMargen { get; } = [];

    public List<clsEntradaGenerarPdf> EntradasPdf { get; } = [];

    public List<string?> NitsFolio { get; } = [];

    /// <summary>Contexto del último turno (un ámbito de DI por turno, como en la API).</summary>
    public clsContextoTurnoAgente Contexto { get; private set; } = new();

    /// <summary>El catálogo de clientes encuentra exactamente a LONGYIN (NIT 900123456, BP 15927).</summary>
    public void CatalogoConLongyin() =>
        Buscador.BuscarAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoBusquedaClientes>.Exito(new clsResultadoBusquedaClientes(true, [new clsClienteEncontrado(NitLongyin, "LONGYIN S.A.S.", "cliente", BpLongyin)])));

    public clsAgenteRunner Runner()
    {
        Contexto = new clsContextoTurnoAgente();
        var herramientas = new clsHerramientasCotizador(
            Contexto, Buscador, Tarifas, Margen, new clsPrecioService(), Ajuste, Pdf, Folios, Correo, Cotizaciones, Options.Create(new clsOpcionesPdf()));
        return new clsAgenteRunner(
            Modelo, Sesiones, Usuarios, Unidad, Reloj, Calendario, new clsEstadoConversacionService(),
            Validador ?? new clsValidadorRespuestaAgente(Options.Create(OpcionesAgente), NullLogger<clsValidadorRespuestaAgente>.Instance, new clsFabricaMedidoresPrueba(), Reloj), Auditoria, Candado, Cotizaciones, Politicas, Contexto, herramientas,
            Options.Create(OpcionesAgente), Options.Create(OpcionesSesiones), Options.Create(OpcionesCorreo), NullLoggerFactory.Instance);
    }

    public Task<clsResultado<clsResultadoTurno>> TurnoAsync(string mensaje, string hilo = Hilo, Guid? usuarioId = null, Guid? cotizacionOrigen = null) =>
        Runner().EjecutarTurnoAsync(new clsSolicitudTurno(mensaje, hilo, hilo, usuarioId ?? Usuario.Id, cotizacionOrigen), TestContext.Current.CancellationToken);

    /// <summary>Estado guardado de la sesión.</summary>
    public Dominio.Agente.clsEstadoConversacion Estado(string hilo = Hilo) =>
        Sesiones.Sesion(hilo)?.EstadoAgente ?? Dominio.Agente.clsEstadoConversacion.Vacio;
}

/// <summary><see cref="System.Diagnostics.Metrics.IMeterFactory"/> mínimo para pruebas: un medidor nuevo por llamada.</summary>
internal sealed class clsFabricaMedidoresPrueba : System.Diagnostics.Metrics.IMeterFactory
{
    private readonly List<System.Diagnostics.Metrics.Meter> _medidores = [];

    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
    {
        var medidor = new System.Diagnostics.Metrics.Meter(options);
        _medidores.Add(medidor);
        return medidor;
    }

    public void Dispose()
    {
        foreach (var medidor in _medidores)
        {
            medidor.Dispose();
        }
    }
}

/// <summary>Candado por sesión en memoria: registra las tomas y, con <see cref="Ocupado"/>, simula otro turno en curso.</summary>
internal sealed class clsCandadoSesionFalso : IClsCandadoSesion
{
    public bool Ocupado { get; set; }

    public List<string> Tomados { get; } = [];

    public int Liberados { get; private set; }

    public Task<IAsyncDisposable?> IntentarTomarAsync(string spaceId, string threadId, TimeSpan espera, CancellationToken cancellationToken = default)
    {
        if (Ocupado)
        {
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        Tomados.Add($"{spaceId}|{threadId}");
        return Task.FromResult<IAsyncDisposable?>(new clsLiberacion(this));
    }

    private sealed class clsLiberacion(clsCandadoSesionFalso candado) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            candado.Liberados++;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary><see cref="IClsSesionRepository"/> en memoria (los eventos quedan al agregarse).</summary>
internal sealed class clsSesionRepositoryMemoria : IClsSesionRepository
{
    private readonly Dictionary<(string, string), clsSesion> _sesiones = [];

    public List<clsEventoConversacion> Eventos { get; } = [];

    public clsSesion? Sesion(string hilo) => _sesiones.GetValueOrDefault((hilo, hilo));

    public void Agregar(clsSesion sesion) => _sesiones[(sesion.SpaceId, sesion.ThreadId)] = sesion;

    public Task<clsSesion?> ObtenerAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sesiones.GetValueOrDefault((spaceId, threadId)));

    public Task<clsSesion> ObtenerOCrearAsync(string spaceId, string threadId, Guid? usuarioId = null, CancellationToken cancellationToken = default)
    {
        if (!_sesiones.TryGetValue((spaceId, threadId), out var sesion))
        {
            sesion = clsSesion.Crear(spaceId, threadId, DateTimeOffset.UtcNow, usuarioId).Valor;
            _sesiones[(spaceId, threadId)] = sesion;
        }

        return Task.FromResult(sesion);
    }

    public Task<clsSesion> TocarAsync(string spaceId, string threadId, DateTimeOffset? cuando = null, CancellationToken cancellationToken = default) =>
        ObtenerOCrearAsync(spaceId, threadId, null, cancellationToken);

    public Task<bool> ArchivarAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<clsSesion>> ListarInactivasAsync(DateTimeOffset umbral, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsSesion>>([]);

    public Task<IReadOnlyList<clsSesion>> ListarPorSpaceAsync(string spaceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsSesion>>([]);

    public void AgregarEvento(clsEventoConversacion evento) => Eventos.Add(evento);

    public Task<IReadOnlyList<clsEventoConversacion>> HistorialAsync(string spaceId, string threadId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<clsEventoConversacion>>(Eventos.Where(e => e.SpaceId == spaceId && e.ThreadId == threadId).OrderBy(e => e.CreatedAt).ToList());
}
