using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Persistencia;

/// <summary>Pasos de <c>specs/features/persistencia.feature</c>.</summary>
[Binding]
public sealed class clsPasosPersistencia(clsContextoEscenario contexto)
{
    [Given("el esquema dbo_cotifly creado con los scripts 0001 a 0010")]
    public async Task DadoElEsquemaCreado()
    {
        var tablas = await clsBaseDatosPruebas.EscalarAsync<int>(clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE' AND TABLE_NAME NOT LIKE 'Respaldo0009[_]%'");
        tablas.Should().Be(24, "HU-021: tras el 0009 son CotizacionesSolicitud, sus rutas, camiones, mercancías y conexos, más las demás tablas de 0001 a 0008 (sin los respaldos Respaldo0009_* que deja el 0009), más CapturasIdempotentes del script 06 (#191) y SapMargenCliente y SapMargenSyncLog del 17 (HU-042) y TarifasSeguro del 20 (HU-045, #264)");
    }

    [Given("el comercial {string} registrado")]
    public async Task DadoElComercialRegistrado(string nombreUsuario)
    {
        var existente = await contexto.ConsultarAsync(s =>
            s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorNombreUsuarioAsync(nombreUsuario));

        if (existente is null)
        {
            existente = clsUsuarioWeb.Crear(nombreUsuario, nombreUsuario, $"{nombreUsuario}@boterosoto.com.co", null, contexto.Reloj.AhoraUtc).Valor;
            var nuevo = existente;
            await contexto.EnAmbitoAsync(s =>
            {
                s.GetRequiredService<IClsUsuarioWebRepository>().Agregar(nuevo);
                return Task.CompletedTask;
            });
        }

        contexto.Usuarios[nombreUsuario] = existente.Id;
    }

    [When("{string} guarda la cotización {string} de la línea {string} por {int} COP")]
    public async Task CuandoGuardaLaCotizacion(string nombreUsuario, string folio, string linea, int precio)
    {
        var politica = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync(linea));
        politica.Should().NotBeNull($"la línea {linea} viene sembrada por el script 0001");

        await GuardarAsync(nombreUsuario, folio, politica!.Id, precio);
    }

    [Given("que {string} guardó la cotización {string}")]
    public Task DadoQueGuardoLaCotizacion(string nombreUsuario, string folio) =>
        GuardarAsync(nombreUsuario, folio, lineaNegocioId: null, precio: 1_000_000m);

    [Given("que {string} guardó la cotización {string} el {string}")]
    public Task DadoQueGuardoLaCotizacionEn(string nombreUsuario, string folio, string momento)
    {
        contexto.Reloj.AhoraUtc = Momento(momento);
        return GuardarAsync(nombreUsuario, folio, lineaNegocioId: null, precio: 1_000_000m);
    }

    [When("{string} intenta guardar otra cotización con el folio {string}")]
    public async Task CuandoIntentaGuardarOtraConElFolio(string nombreUsuario, string folio)
    {
        try
        {
            await GuardarAsync(nombreUsuario, folio, lineaNegocioId: null, precio: 2_000_000m);
        }
        catch (clsViolacionUnicidadException ex)
        {
            contexto.UltimoError = ex;
        }
    }

    [When("{string} consulta sus cotizaciones")]
    public async Task CuandoConsultaSusCotizaciones(string nombreUsuario)
    {
        var cotizaciones = await contexto.ConsultarAsync(s =>
            s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuarioAsync(Usuario(nombreUsuario)));
        contexto.FoliosConsultados = [.. cotizaciones.Select(c => c.Folio)];
    }

    [When("{string} marca como ganada la cotización {string} el {string}")]
    public async Task CuandoMarcaComoGanadaEn(string nombreUsuario, string folio, string momento)
    {
        contexto.Reloj.AhoraUtc = Momento(momento);
        (await MarcarGanadaAsync(nombreUsuario, folio)).Should().NotBeNull();
    }

    [Then("al consultar el folio {string} se obtiene la línea {string} y el precio {int}")]
    public async Task EntoncesSeObtieneLaLineaYElPrecio(string folio, string linea, int precio)
    {
        var (cotizacion, lineaLeida) = await contexto.ConsultarAsync(async s =>
        {
            var leida = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio);
            var politica = await s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorIdAsync(leida!.PrimeraRuta!.LineaNegocioId!.Value);
            return (leida, politica?.LineaNegocio);
        });

        lineaLeida.Should().Be(linea);
        cotizacion.PrecioFinal.Should().Be(precio);
    }

    [Then("la cotización {string} está en estado de envío {string} y versión {int}")]
    public async Task EntoncesEstaEnEstadoYVersion(string folio, string estado, int version)
    {
        var cotizacion = await Leer(folio);

        clsCodigoEnum.ACodigo(cotizacion.EstadoEnvio!.Value).Should().Be(estado);
        cotizacion.Version.Should().Be(version);
    }

    [Then("la operación se rechaza con el código {string}")]
    public void EntoncesSeRechazaConElCodigo(string codigo)
    {
        contexto.UltimoError.Should().BeOfType<clsViolacionUnicidadException>()
            .Which.Error.Codigo.Should().Be(codigo);
    }

    [Then("no ve el folio {string}")]
    public void EntoncesNoVeElFolio(string folio) => contexto.FoliosConsultados.Should().NotContain(folio);

    [Then("{string} no puede marcar como ganada la cotización {string}")]
    public async Task EntoncesNoPuedeMarcar(string nombreUsuario, string folio)
    {
        (await MarcarGanadaAsync(nombreUsuario, folio)).Should().BeNull("una cotización ajena se trata como inexistente");
        (await Leer(folio)).Ganada.Should().BeNull();
    }

    [Then("{string} sí puede marcar como ganada la cotización {string}")]
    public async Task EntoncesSiPuedeMarcar(string nombreUsuario, string folio)
    {
        (await MarcarGanadaAsync(nombreUsuario, folio)).Should().NotBeNull();
        (await Leer(folio)).Ganada.Should().BeTrue();
    }

    [Then("el updatedAt de la cotización {string} es {string}")]
    public async Task EntoncesElUpdatedAtEs(string folio, string momento) =>
        (await Leer(folio)).UpdatedAt.Should().Be(Momento(momento));

    [Then("el createdAt de la cotización {string} sigue siendo {string}")]
    public async Task EntoncesElCreatedAtSigueSiendo(string folio, string momento) =>
        (await Leer(folio)).CreatedAt.Should().Be(Momento(momento));

    private Guid Usuario(string nombreUsuario) =>
        contexto.Usuarios.TryGetValue(nombreUsuario, out var id)
            ? id
            : throw new InvalidOperationException($"El comercial '{nombreUsuario}' no está registrado en el escenario.");

    private Task GuardarAsync(string nombreUsuario, string folio, int? lineaNegocioId, decimal precio)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            Version = VersionDelFolio(folio),
            UsuarioId = Usuario(nombreUsuario),
        }).Valor;
        // HU-021: la línea de negocio y el precio son de la ruta; el precio de la cotización, la suma de sus rutas.
        cotizacion.RegistrarCotizacionChat(lineaNegocioId, null, null, null, null, null, precio);

        return contexto.EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion);
            return Task.CompletedTask;
        });
    }

    /// <summary>Marca como ganada con el caso de uso de seguimiento (#11); <c>null</c> si no es del comercial.</summary>
    private async Task<clsCotizacionSolicitud?> MarcarGanadaAsync(string nombreUsuario, string folio)
    {
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsSeguimientoService>()
            .MarcarAsync(Usuario(nombreUsuario), folio, true, null));
        return resultado.EsExitoso ? resultado.Valor : null;
    }

    private async Task<clsCotizacionSolicitud> Leer(string folio)
    {
        var cotizacion = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio));
        cotizacion.Should().NotBeNull();
        return cotizacion!;
    }

    /// <summary>Versión según el sufijo <c>-vN</c> del folio (formato vigente del backlog); sin sufijo es la 1.</summary>
    private static int VersionDelFolio(string folio)
    {
        var sufijo = folio.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        return sufijo > 0 && int.TryParse(folio.AsSpan(sufijo + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : 1;
    }

    private static DateTimeOffset Momento(string texto) =>
        DateTimeOffset.Parse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None);
}
