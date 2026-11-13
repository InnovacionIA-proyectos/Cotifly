using Cotifly.Aplicacion.Administracion.Services;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Metricas.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aplicacion.Tests;

public sealed class clsDependenciasAplicacionTests
{
    [Fact]
    public void AddAplicacionDevuelveLaMismaColeccionParaEncadenar()
    {
        var servicios = new ServiceCollection();

        var devuelta = servicios.AddAplicacion();

        devuelta.Should().BeSameAs(servicios);
    }

    [Fact]
    public void AddAplicacionConColeccionNulaLanzaArgumentNull()
    {
        IServiceCollection servicios = null!;

        var registrar = () => servicios.AddAplicacion();

        registrar.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(typeof(IClsPrecioService), typeof(clsPrecioService), ServiceLifetime.Singleton)]
    [InlineData(typeof(IClsMargenResolverService), typeof(clsMargenResolverService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsVistaPreviaCorreoService), typeof(clsVistaPreviaCorreoService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsDestinatariosPreviosService), typeof(clsDestinatariosPreviosService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsTelefonoComercialService), typeof(clsTelefonoComercialService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsSeguimientoService), typeof(clsSeguimientoService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsCatalogoMotivosPerdida), typeof(clsCatalogoMotivosPerdida), ServiceLifetime.Singleton)]
    [InlineData(typeof(IClsExportacionCotizacionService), typeof(clsExportacionCotizacionService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsAuditoriaService), typeof(clsAuditoriaService), ServiceLifetime.Singleton)]
    [InlineData(typeof(clsCandadoEnvios), typeof(clsCandadoEnvios), ServiceLifetime.Singleton)]
    [InlineData(typeof(clsVerificadorIdentidad), typeof(clsVerificadorIdentidad), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsLoginClienteProService), typeof(clsLoginClienteProService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsRefreshTokenService), typeof(clsRefreshTokenService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsLogoutService), typeof(clsLogoutService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsRevocacionAccesos), typeof(clsCacheTokensRevocados), ServiceLifetime.Singleton)]
    [InlineData(typeof(IClsMisCotizacionesService), typeof(clsMisCotizacionesService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsCotizacionesRegionalService), typeof(clsCotizacionesRegionalService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsNuevaVersionService), typeof(clsNuevaVersionService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsGeneradorFolioService), typeof(clsGeneradorFolioService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsDescargaPdfService), typeof(clsDescargaPdfService), ServiceLifetime.Scoped)]
    [InlineData(typeof(Cotifly.Aplicacion.Cotizaciones.Services.IClsAccesoCotizacionAjena), typeof(Cotifly.Aplicacion.Cotizaciones.Services.clsAccesoCotizacionAjenaService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsLineasNegocioAdminService), typeof(clsLineasNegocioAdminService), ServiceLifetime.Scoped)]
    [InlineData(typeof(Cotifly.Aplicacion.Margen.Services.IClsSapMargenAdminService), typeof(Cotifly.Aplicacion.Margen.Services.clsSapMargenAdminService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsLineasNegocioCatalogoService), typeof(clsLineasNegocioCatalogoService), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsAsistenteFolioRunner), typeof(clsAsistenteFolioRunner), ServiceLifetime.Scoped)]
    [InlineData(typeof(clsContextoAsistenteFolio), typeof(clsContextoAsistenteFolio), ServiceLifetime.Scoped)]
    [InlineData(typeof(clsHerramientasAsistenteFolio), typeof(clsHerramientasAsistenteFolio), ServiceLifetime.Scoped)]
    [InlineData(typeof(IClsValidadorRespuestaAgente), typeof(clsValidadorRespuestaAgente), ServiceLifetime.Singleton)]
    [InlineData(typeof(Cotifly.Aplicacion.Cubicacion.Interfaces.IClsCalculoCubicajeService), typeof(Cotifly.Aplicacion.Cubicacion.Services.clsCalculoCubicajeService), ServiceLifetime.Scoped)]
    public void AddAplicacionRegistraLosCasosDeUso(Type servicio, Type implementacion, ServiceLifetime ciclo)
    {
        var servicios = new ServiceCollection();

        servicios.AddAplicacion();

        servicios.Should().ContainSingle(d => d.ServiceType == servicio)
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationType == implementacion && d.Lifetime == ciclo);
    }

    // El agente cotizador legado, el slot-filling y el procesamiento de Google Chat no se registran.
    [Theory]
    [InlineData("Cotifly.Aplicacion.Agente.Services.IClsAgenteRunner")]
    [InlineData("Cotifly.Aplicacion.Agente.Services.IClsEstadoConversacionService")]
    [InlineData("Cotifly.Aplicacion.Agente.Services.IClsProcesadorEventoChat")]
    [InlineData("Cotifly.Aplicacion.Agente.Herramientas.clsHerramientasCotizador")]
    [InlineData("Cotifly.Aplicacion.Agente.Services.clsContextoTurnoAgente")]
    [InlineData("Cotifly.Aplicacion.Agente.Services.clsOpcionesChat")]
    public void ElAgenteLegadoYGoogleChatYaNoExisten(string tipo) =>
        typeof(clsDependenciasAplicacion).Assembly.GetType(tipo).Should().BeNull();

    /// <summary>El puerto de la cubicación rápida de Nexo no se registra; "Calcular cubicaje" usa el cubicador.</summary>
    [Fact]
    public void ElPuertoDeLaCubicacionDeNexoYaNoExiste() =>
        typeof(clsDependenciasAplicacion).Assembly.GetType("Cotifly.Aplicacion.Cubicacion.Interfaces.IClsCubicacionProvider").Should().BeNull();

    /// <summary>Los casos de uso auditados se registran como decorador sobre la implementación concreta.</summary>
    [Theory]
    [InlineData(typeof(IClsAjusteMargenService), typeof(clsAjusteMargenService), typeof(clsAjusteMargenServiceAuditado))]
    [InlineData(typeof(IClsGenerarPdfService), typeof(clsGenerarPdfService), typeof(clsGenerarPdfServiceAuditado))]
    [InlineData(typeof(IClsCorreoService), typeof(clsCorreoService), typeof(clsCorreoServiceAuditado))]
    public void LosCasosDeUsoAuditadosSeRegistranConSuDecorador(Type servicio, Type implementacion, Type decorador)
    {
        var servicios = new ServiceCollection();

        servicios.AddAplicacion();

        servicios.Should().ContainSingle(d => d.ServiceType == servicio)
            .Which.Should().Match<ServiceDescriptor>(d => d.ImplementationFactory != null && d.Lifetime == ServiceLifetime.Scoped);
        servicios.Should().ContainSingle(d => d.ServiceType == implementacion && d.Lifetime == ServiceLifetime.Scoped);
        decorador.GetInterfaces().Should().Contain(servicio);
    }
}
