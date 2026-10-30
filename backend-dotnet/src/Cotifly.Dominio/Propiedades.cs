using System.Runtime.CompilerServices;

// Las pruebas arman escenarios de costeo con los métodos internos de los camiones y las rutas (estado, costos y conexos sin recalcular);
// el código de producción solo cambia el agregado por la raíz (AplicarFotoNexo, CambiarConexos, ConservarConexos; ADR-019).
// La persistencia recarga el agregado en la misma instancia (ReemplazarRutasLeidas): no es una operación de negocio.
[assembly: InternalsVisibleTo("Cotifly.Infraestructura")]
[assembly: InternalsVisibleTo("Cotifly.Dominio.Tests")]
[assembly: InternalsVisibleTo("Cotifly.Aplicacion.Tests")]
[assembly: InternalsVisibleTo("Cotifly.Infraestructura.Tests")]
[assembly: InternalsVisibleTo("Cotifly.Api.Tests")]
[assembly: InternalsVisibleTo("Cotifly.Aceptacion.Tests")]
